using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using NativeWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace QoeDevice {
    public static class QoeLog {
        public static void Event(string topic, string msg) => Debug.Log($"[Qoe] {topic}: {msg}");
        public static void Warn (string topic, string msg) => Debug.LogWarning($"[Qoe] {topic}: {msg}");
        public static void Err  (string topic, string msg) => Debug.LogError($"[Qoe] {topic}: {msg}");
    }

    public static class WsType {
        public const string Hello = "hello";
        public const string StartTask = "start_task";
        public const string Ready = "ready";
        public const string Rejected = "rejected";
    }

    public class WsEnvelope { public string type; public JObject data; }

    // Briefing sits between teleport and the timed run: the subject has been
    // placed in front of the agent and is reading the on-HUD context, with the
    // conversation gated closed, until they press Start.
    public enum DevicePhase { Idle, LoadingTask, TaskReceived, Briefing, RunningTask }

    public class QoeDeviceClient : MonoBehaviour {
        // Single live client. Set in Awake so the audio pipelines can reach it via
        // NotifyConversationOver without holding a reference. Only one device
        // client exists in QoE_Shell.
        public static QoeDeviceClient instance;

        [Header("Server")]
        public string serverHost = "192.168.1.50";
        public int serverPort = 8080;

        [Header("Device identity (sent in `hello`)")]
        public string deviceKind = "quest3-unity";
        public string deviceName = "Quest 3 (Unity)";

        [Header("UI")]
        [Tooltip("Empty RectTransform under a Canvas. UI is built here at runtime.")]
        public RectTransform rootContainer;
        public bool followCamera = true;
        public float followDistance = 1.0f;
        public Transform followCameraTarget;

        [Header("Rating client (optional)")]
        public QoeRatingClient ratingClient;

        [Header("Debug")]
        [Tooltip("Shows log panel, task grid, and Preview rating button. Off for subject-facing builds.")]
        public bool debugMode = true;

        [Header("Log panel")]
        public int logMaxLines = 12;

        [Header("Task teleport")]
        [Tooltip("Player root to teleport.")]
        public Transform playerTransform;
        [Tooltip("Spawn points by task index. [0]=Training, [1..3]=City friend/clerk/manager, " +
                 "[4..6]=Hotel receptionist/maintenance/waiter, [7..9]=Museum host/volunteer1/volunteer2. " +
                 "Assign each scene's Agents/<role>/SpawnPoint in the Inspector.")]
        public Transform[] taskSpawnPoints = new Transform[10];
        [Tooltip("Where the player goes when a run ends. Defaults to world origin if unassigned.")]
        public Transform neutralPoint;

        [Header("Scene culling (performance)")]
        [Tooltip("Optional. The four scene roots in QoE_Shell: [0]=Training, [1]=City, " +
                 "[2]=Hotel, [3]=Museum. When assigned, only the scene the player is " +
                 "teleported into stays active; the other three are SetActive(false). " +
                 "Loading all four at once is ~70M triangles — the subject only ever " +
                 "occupies one agent area at a time, so this is the cheapest big win. " +
                 "Leave all four empty to disable culling (no behaviour change).")]
        public GameObject[] sceneRoots = new GameObject[4];
        // Maps task index → sceneRoots index: 0=Training, 1-3=City, 4-6=Hotel, 7-9=Museum.
        static readonly int[] kTaskSceneRoot = { 0, 1, 1, 1, 2, 2, 2, 3, 3, 3 };

        [Header("Screen fade (VR comfort)")]
        [Tooltip("Auto-created on this GameObject if left null.")]
        public ScreenFader fader;
        public float fadeDuration = 0.3f;

        [Header("Debug run")]
        [Tooltip("Run duration (s) used by the debug Task buttons. The real study " +
                 "uses max_condition_duration_s from the operator instead.")]
        public int debugRunDurationS = 120;

        string HttpBase    => $"http://{serverHost}:{serverPort}";
        string WsDeviceUrl => $"ws://{serverHost}:{serverPort}/device";

        WebSocket ws;
        bool wsClosedByUs;
        bool isConnecting;
        DevicePhase phase = DevicePhase.Idle;
        bool IsWsOpen => ws != null && ws.State == WebSocketState.Open;

        string activeSid;
        int activeRunId;
        string activeLabel;
        int maxDurationS;
        int activeTaskIndex; // null/training → 0, task_number N → N
        bool isDebugRun;     // true when started by a debug Task button (no operator WS / no /end-condition)
        Coroutine runCo;
        Coroutine connectTimeoutCo;
        const float ConnectTimeoutS = 10f;

        readonly Queue<string> logLines = new();
        readonly ConcurrentQueue<Action> mainQ = new();

        readonly QoeUI ui = new();
        PressDownButton connectButton, disconnectButton, sendReadyButton, endRunEarlyButton, previewRatingButton, startButton;

        static readonly string[] kTaskLabels = {
            "Training", "Task 1", "Task 2", "Task 3", "Task 4",
            "Task 5", "Task 6", "Task 7", "Task 8", "Task 9",
        };
        // Backend scene to /refresh per task index. The backend keeps one global
        // conversation handler keyed by scene name (transition_prompts_<scene>.py),
        // so this selects which scene's agent1/2/3 prompts + voices answer. The
        // City scene's backend module is named "Shirts" (transition_prompts_Shirts.py).
        // Which of agent1/2/3 actually answers is chosen by the proximity
        // ActivationZone in front of the player, not by this index.
        static readonly string[] kTaskBackendScenes = {
            "Training",                  // 0
            "Shirts", "Shirts", "Shirts",// 1–3 City: friend, clerk, manager
            "Hotel", "Hotel", "Hotel",   // 4–6 Hotel: receptionist, maintenance, waiter
            "Museum", "Museum", "Museum",// 7–9 Museum: host, volunteer1, volunteer2
        };

        // Pre-conversation context shown on the HUD after teleport, by task index.
        // Sets the scene for the subject (who they're standing in front of and
        // roughly what to chat about) without scripting the conversation — the
        // agents are open-ended one-offs. Kept to 1–2 short sentences so it reads
        // at a glance. Edit freely; index order matches kTaskLabels / spawn points.
        static readonly string[] kTaskBriefings = {
            // 0 Training
            "Welcome! In this study you'll have a series of short conversations with virtual people. This first one is just practice.\n\n" +
            "How it works: each conversation has a time limit, shown as a countdown — it's only an upper limit, so don't rush. When you feel the conversation is finished, simply say goodbye and the person will wrap up; a Done button will then appear for you to continue.\n\n" +
            "After each conversation you'll be asked a few quick questions about how it felt, then you'll move on to the next one.\n\n" +
            "Right now you're with Alfred, a friendly assistant. Say hello and chat with him to get comfortable — to speak, press the microphone button on your right controller, and press it again when you finish talking.",
            // 1–3 City (Shirts)
            "You're at your friend Sage's place, just hanging out. Catch up with them and chat about whatever comes to mind.",
            "You're in a clothing store, talking to Niko, the shop clerk. Strike up a conversation — ask about the store or whatever you're curious about.",
            "You're at the back of a clothing store, talking to the store manager. Have a conversation about the store or anything you like.",
            // 4–6 Hotel
            "You're at the front desk of Hotel 333, talking to Hazel, the receptionist. Chat with them about your stay, the hotel, or the area.",
            "You're on the hotel floor, talking to Justin, a maintenance worker on a break. Make conversation about the hotel or his work.",
            "You're at the hotel's restaurant, talking to Luka, the waiter. Chat about the food, the restaurant, or whatever you'd like.",
            // 7–9 Museum
            "You're at the entrance of the Millennium Museum, talking to Emma, the receptionist. Ask about visiting the museum or just make conversation.",
            "You're at the Cyrus cylinder exhibit, talking to Aleksander, a volunteer. Chat with him about the exhibit or whatever interests you.",
            "You're at the civil rights exhibit, talking to Tammy, a volunteer. Have a conversation about the exhibit or anything you're curious about.",
        };

        // Suggested talking-points per task — shown in the briefing AND kept on
        // the HUD during the run as a small reminder list, so a subject who runs
        // out of things to say has prompts to fall back on. These are NOT tracked
        // or required (no checkmarks, no completion); they're conversational
        // scaffolding only, repurposed from the original quest goals into pure
        // "things to talk about". One string per task; lines are split on '|'.
        static readonly string[] kTaskTalkingPoints = {
            // 0 Training
            "How Alfred is doing|What this place is|What to expect in the study",
            // 1–3 City (Shirts)
            "How they've been lately|Tell them something going on with you|Make plans to hang out",
            "What the store sells|Returning or exchanging an item|Ask for a recommendation",
            "How they run the store|A return or refund|Ask about the clothing range",
            // 4–6 Hotel
            "Checking in / your room|Things to do nearby|The hotel's restaurant",
            "What they're working on|How the hotel is run|Anything that needs fixing",
            "Today's specials|Food you like or avoid|Recommendations",
            // 7–9 Museum
            "Visiting the museum today|What's on display|Student admission",
            "What the Cyrus cylinder is|Why it matters|Who Cyrus was",
            "The civil rights movement|Martin Luther King|The Montgomery Bus Boycott",
        };

        string BriefingFor(int taskIndex) {
            string intro = (taskIndex >= 0 && taskIndex < kTaskBriefings.Length)
                ? kTaskBriefings[taskIndex]
                : "Press Start when you're ready to begin talking with the agent in front of you.";
            string points = TalkingPointsBlock(taskIndex);
            if (string.IsNullOrEmpty(points)) return intro;
            return intro + "\n\nYou could talk about:\n" + points;
        }

        // The talking-points formatted as a bulleted block. Empty string if none.
        string TalkingPointsBlock(int taskIndex) {
            if (taskIndex < 0 || taskIndex >= kTaskTalkingPoints.Length) return "";
            var raw = kTaskTalkingPoints[taskIndex];
            if (string.IsNullOrEmpty(raw)) return "";
            var parts = raw.Split('|');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++) {
                if (i > 0) sb.Append('\n');
                sb.Append("• ").Append(parts[i].Trim());
            }
            return sb.ToString();
        }

        static readonly Color kBlue         = new(0.16f, 0.5f,  0.95f);
        static readonly Color kGray         = new(0.55f, 0.55f, 0.6f);
        static readonly Color kGreen        = new(0.2f,  0.7f,  0.35f);
        static readonly Color kRed          = new(0.8f,  0.35f, 0.25f);
        static readonly Color kTaskBtn      = new(0.3f,  0.4f,  0.55f);
        static readonly Color kPreview      = new(0.45f, 0.45f, 0.5f);
        static readonly Color kMicRecording = new(0.9f,  0.2f,  0.2f);
        static readonly Color kMicIdle      = new(0.32f, 0.32f, 0.38f);

        TMP_Text logText;
        TMP_Text timerText;
        TMP_Text briefingText;
        GameObject briefingGo;
        TMP_Text pointsText;
        GameObject pointsGo;
        PressDownButton doneButton;
        // True once the agent has wrapped up the conversation (user said goodbye).
        // The Done button stays hidden until this flips, so it isn't offered from
        // the start of the run. Reset at the start of each run.
        bool conversationWrappedUp;
        Image    micDot;
        TMP_Text micLabel;
        TMP_Text connStatusText;
        TMP_Text errorText;
        GameObject topLeftGo;
        GameObject controlsGo;
        GameObject taskGridGo;
        GameObject logPanelGo;
        GameObject connStatusGo;
        GameObject errorGo;
        RectTransform centerRegion;
        MicrophoneHandler micHandler;
        string lastError = "";

        void Awake()     { instance = this; }
        void OnEnable()  { Application.logMessageReceived += OnUnityLog; }
        void OnDisable() { Application.logMessageReceived -= OnUnityLog; }

        void Start() {
            QoeLog.Event("init", $"server={HttpBase} kind={deviceKind}");
            EnsureFader();
            BuildUi();
            SetHud("Ready — press Connect");
            UpdateButtonStates();
        }

        void EnsureFader() {
            if (fader == null) fader = GetComponent<ScreenFader>();
            if (fader == null) fader = gameObject.AddComponent<ScreenFader>();
        }

        void BuildUi() {
            if (rootContainer == null) {
                QoeLog.Err("ui", "rootContainer not assigned — cannot build device UI");
                return;
            }
            for (int i = rootContainer.childCount - 1; i >= 0; i--) {
                var child = rootContainer.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
            StripComponent<VerticalLayoutGroup>(rootContainer.gameObject);
            StripComponent<HorizontalLayoutGroup>(rootContainer.gameObject);
            StripComponent<ContentSizeFitter>(rootContainer.gameObject);

            rootContainer.anchorMin = Vector2.zero; rootContainer.anchorMax = Vector2.one;
            rootContainer.offsetMin = Vector2.zero; rootContainer.offsetMax = Vector2.zero;

            Canvas.ForceUpdateCanvases();
            float rootW = rootContainer.rect.width;
            float rootH = rootContainer.rect.height;
            ui.scale = rootW > 0 ? Mathf.Clamp(rootW / 600f, 0.05f, 4f) : 1f;

            BuildTopLeftCluster(rootContainer);
            BuildConnStatus(rootContainer);
            BuildControlsCluster(rootContainer);
            BuildCenterRegion(rootContainer);
            BuildBriefingPanel(rootContainer);
            BuildPointsPanel(rootContainer);
            BuildErrorBanner(rootContainer);
            if (debugMode) BuildTaskGrid(rootContainer, rootW, rootH);
            if (debugMode) BuildLogPanel(rootContainer);

            if (ratingClient != null) {
                ratingClient.BuildUi(centerRegion, debugMode);
                ratingClient.OnFormVisibilityChanged = _ => UpdateUiVisibility();
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(rootContainer);
            AttachCanvasFollower();
            UpdateButtonStates();
            UpdateUiVisibility();
        }

        void UpdateUiVisibility() {
            if (rootContainer == null) return;
            rootContainer.gameObject.SetActive(true);

            bool ratingVisible = ratingClient != null && ratingClient.IsFormVisible;
            bool running       = phase == DevicePhase.RunningTask;
            bool briefing      = phase == DevicePhase.Briefing;
            bool canConnect    = phase == DevicePhase.Idle && !isConnecting && !IsWsOpen;
            bool taskReceived  = phase == DevicePhase.TaskReceived;

            // Pre-conversation briefing panel — only while the subject is reading
            // the context, before they press Start. The debug Task buttons enter
            // Briefing too, so manual testing shows it exactly like a real run.
            if (briefingGo != null) briefingGo.SetActive(briefing);

            // Talking-points panel — during the whole run. The Done button inside
            // it stays hidden until the agent wraps up (conversationWrappedUp).
            if (pointsGo != null) pointsGo.SetActive(running);
            SetActive(doneButton, running && conversationWrappedUp);

            // Mic indicator + timer cluster — shown while a task is running. The
            // debug Task buttons enter RunningTask too (DebugStartTask), so this
            // is visible during manual testing exactly as in a real run.
            if (topLeftGo    != null) topLeftGo.SetActive(running);
            if (logPanelGo   != null) logPanelGo.SetActive(debugMode);
            if (taskGridGo   != null) taskGridGo.SetActive(debugMode);
            if (connStatusGo != null) connStatusGo.SetActive(true);
            if (errorGo      != null) errorGo.SetActive(!string.IsNullOrEmpty(lastError));

            SetActive(connectButton,       debugMode || (canConnect   && !ratingVisible));
            SetActive(disconnectButton,    debugMode);
            SetActive(sendReadyButton,     debugMode || (taskReceived && !ratingVisible));
            SetActive(endRunEarlyButton,   debugMode || running);
            SetActive(previewRatingButton, debugMode);
            if (controlsGo != null) controlsGo.SetActive(debugMode || canConnect || taskReceived || running || briefing);
        }

        static void SetActive(PressDownButton b, bool on) {
            if (b != null) b.gameObject.SetActive(on);
        }

        static void StripComponent<T>(GameObject go) where T : Component {
            var c = go.GetComponent<T>();
            if (c == null) return;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
        }

        void AttachCanvasFollower() {
            if (!Application.isPlaying || !followCamera || rootContainer == null) return;
            var canvas = rootContainer.GetComponentInParent<Canvas>();
            if (canvas == null) { QoeLog.Warn("ui", "No Canvas in parent chain — skipping LazyCameraFollow"); return; }
            var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            var follow = root.GetComponent<LazyCameraFollow>();
            if (follow == null) follow = root.gameObject.AddComponent<LazyCameraFollow>();
            follow.distance = followDistance;
            if (followCameraTarget != null) follow.cam = followCameraTarget;
        }

        [ContextMenu("Device: build controls UI")]
        void Editor_BuildControlsUi() {
            BuildUi();
            UpdateButtonStates();
        }

        [ContextMenu("Device: build rating UI")]
        void Editor_BuildRatingUi() {
            BuildUi();
            UpdateButtonStates();
            if (ratingClient == null) { QoeLog.Warn("ui", "ratingClient not assigned"); return; }
            ratingClient.LoadDebugPreview();
        }

        [ContextMenu("Device: clear UI")]
        void Editor_ClearUi() {
            if (rootContainer == null) return;
            for (int i = rootContainer.childCount - 1; i >= 0; i--) {
                var child = rootContainer.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
            topLeftGo = controlsGo = taskGridGo = logPanelGo = connStatusGo = errorGo = briefingGo = pointsGo = null;
            timerText = null; micDot = null; micLabel = null; centerRegion = null; briefingText = null; pointsText = null;
        }

        // Top-left: recording light (gray idle / red recording) + "REC" label + timer.
        // Only visible while a task is running.
        void BuildTopLeftCluster(RectTransform parent) {
            var region = ui.BuildAnchoredRegion(parent, "TopLeft", new Vector2(0f, 0.84f), new Vector2(0.42f, 1f), ui.Sx(6));
            topLeftGo = region.gameObject;
            var hg = region.gameObject.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = ui.Sx(6);
            hg.childForceExpandWidth = false; hg.childForceExpandHeight = true;
            hg.childControlWidth = true; hg.childControlHeight = true;
            hg.childAlignment = TextAnchor.MiddleLeft;

            var dotGo = new GameObject("MicDot", typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(region, false);
            micDot = dotGo.GetComponent<Image>();
            micDot.color = kMicIdle;
            var dotLe = dotGo.AddComponent<LayoutElement>();
            int dot = ui.Sx(18);
            dotLe.minWidth = dot; dotLe.preferredWidth = dot; dotLe.flexibleWidth = 0;
            dotLe.minHeight = dot; dotLe.preferredHeight = dot; dotLe.flexibleHeight = 0;

            // Always visible: shows "MIC" (idle) / "REC" (recording). It used to be
            // hidden at idle, which left just the gray dot + empty timer — reading
            // as a featureless gray blob.
            micLabel = ui.BuildLabel(region, "MIC", 13, FontStyles.Bold, kMicIdle, TextAlignmentOptions.Left);
            micLabel.enableWordWrapping = false;
            var mle = micLabel.gameObject.AddComponent<LayoutElement>();
            mle.minWidth = ui.Sx(30); mle.preferredWidth = ui.Sx(30); mle.flexibleWidth = 0;
            mle.minHeight = ui.Sx(18); mle.preferredHeight = ui.Sx(18);

            timerText = ui.BuildLabel(region, "0:00", 22, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
            timerText.enableWordWrapping = false;
            var tle = timerText.gameObject.AddComponent<LayoutElement>();
            tle.flexibleWidth = 1f; tle.minHeight = ui.Sx(28); tle.preferredHeight = ui.Sx(28);
        }

        // Top-right: gray Connected/Connecting…/Disconnected, identical in both modes.
        void BuildConnStatus(RectTransform parent) {
            var region = ui.BuildAnchoredRegion(parent, "ConnStatus", new Vector2(0.44f, 0.88f), new Vector2(1f, 1f), ui.Sx(4));
            connStatusGo = region.gameObject;
            connStatusText = ui.BuildLabel(region, "", 13, FontStyles.Bold, new Color(0.6f, 0.65f, 0.72f), TextAlignmentOptions.TopRight);
            connStatusText.enableWordWrapping = false;
            connStatusText.overflowMode = TextOverflowModes.Ellipsis;
            QoeUI.StretchToParent((RectTransform)connStatusText.transform);
        }

        void BuildControlsCluster(RectTransform parent) {
            float topAnchor = debugMode ? 0.42f : 0.72f;
            var region = ui.BuildAnchoredRegion(parent, "Controls", new Vector2(0.78f, topAnchor), new Vector2(1f, 0.86f), ui.Sx(4));
            controlsGo = region.gameObject;
            var vlg = region.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = ui.Sx(4);
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = true;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.UpperCenter;

            connectButton     = ui.BuildButton(region, "Connect",    kBlue,  16, ConnectManual);
            disconnectButton  = ui.BuildButton(region, "Disconnect", kGray,  16, DisconnectManual);
            sendReadyButton   = ui.BuildButton(region, "Ready",      kGreen, 16, SendReadyManual);
            endRunEarlyButton = ui.BuildButton(region, "End Run",    kRed,   16, EndRunEarly);
            int btnH = ui.Sx(22);
            foreach (var b in new[] { connectButton, disconnectButton, sendReadyButton, endRunEarlyButton }) {
                var le = b.GetComponent<LayoutElement>();
                le.minHeight = btnH; le.preferredHeight = btnH; le.flexibleHeight = 1f;
            }

            if (debugMode && ratingClient != null) {
                previewRatingButton = ui.BuildButton(region, "Preview rating", kPreview, 14, ratingClient.LoadDebugPreview);
                var le = previewRatingButton.GetComponent<LayoutElement>();
                int ph = ui.Sx(20);
                le.minHeight = ph; le.preferredHeight = ph; le.flexibleHeight = 1f;
            }
        }

        void BuildCenterRegion(RectTransform parent) {
            Vector2 min = debugMode ? new Vector2(0.12f, 0.18f) : new Vector2(0.08f, 0.06f);
            Vector2 max = debugMode ? new Vector2(0.73f, 0.82f) : new Vector2(0.92f, 0.82f);
            centerRegion = ui.BuildAnchoredRegion(parent, "Center", min, max, ui.Sx(4));
            var vlg = centerRegion.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = ui.Sx(6);
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.UpperCenter;
        }

        // Overlays the center when there's an error; built separately so rating-client
        // rebuilds of the center don't destroy it.
        void BuildErrorBanner(RectTransform parent) {
            var region = ui.BuildAnchoredRegion(parent, "ErrorBanner", new Vector2(0.12f, 0.4f), new Vector2(0.88f, 0.62f), ui.Sx(6));
            errorGo = region.gameObject;
            errorText = ui.BuildLabel(region, "", 18, FontStyles.Bold, new Color(0.95f, 0.45f, 0.4f), TextAlignmentOptions.Center);
            errorText.enableWordWrapping = true;
            QoeUI.StretchToParent((RectTransform)errorText.transform);
        }

        // Pre-conversation briefing: a centered panel with the per-task context
        // text and a big Start button. Shown only during the Briefing phase (after
        // teleport, before the timed run). Pressing Start opens the conversation
        // gate and begins the timer — see OnStartPressed. Built separately from
        // the rating client's center region so neither clobbers the other.
        void BuildBriefingPanel(RectTransform parent) {
            // Near-full-canvas so even the long training briefing (timer +
            // questionnaire explanation + talking-points) has room.
            var region = ui.BuildAnchoredRegion(parent, "Briefing", new Vector2(0.04f, 0.08f), new Vector2(0.96f, 0.94f), ui.Sx(6));
            briefingGo = region.gameObject;
            var bg = region.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.6f);

            var vlg = region.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = ui.Sx(10);
            vlg.padding = new RectOffset(ui.Sx(16), ui.Sx(16), ui.Sx(14), ui.Sx(14));
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.UpperCenter;

            // Text takes the available space and auto-shrinks to fit rather than
            // overflowing. Left-aligned so the "• talking point" bullets read as a
            // list; word-wrap on. Floor of ~10pt keeps it legible in-headset.
            briefingText = ui.BuildLabel(region, "", 17, FontStyles.Normal, Color.white, TextAlignmentOptions.TopLeft);
            briefingText.enableWordWrapping = true;
            briefingText.enableAutoSizing = true;
            briefingText.fontSizeMin = ui.Sx(10);
            briefingText.fontSizeMax = ui.Sx(17);
            var tle = briefingText.gameObject.AddComponent<LayoutElement>();
            tle.flexibleHeight = 1f;

            startButton = ui.BuildButton(region, "Start", kGreen, 20, OnStartPressed);
            var sle = startButton.GetComponent<LayoutElement>();
            int sh = ui.Sx(46);
            sle.minHeight = sh; sle.preferredHeight = sh; sle.flexibleHeight = 0f;
        }

        // During-run panel: the (untracked) talking-points reminder plus the
        // subject-facing Done button. Lower-left so it clears the top-left timer/
        // mic cluster and the right-side controls. Visible only while RunningTask.
        // In debug the task grid + log occupy the bottom, so this sits a little
        // higher and narrower to avoid them.
        void BuildPointsPanel(RectTransform parent) {
            Vector2 min = debugMode ? new Vector2(0f, 0.18f) : new Vector2(0f, 0.0f);
            Vector2 max = debugMode ? new Vector2(0.34f, 0.82f) : new Vector2(0.34f, 0.82f);
            var region = ui.BuildAnchoredRegion(parent, "Points", min, max, ui.Sx(6));
            pointsGo = region.gameObject;
            var bg = region.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.4f);

            var vlg = region.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = ui.Sx(8);
            vlg.padding = new RectOffset(ui.Sx(10), ui.Sx(10), ui.Sx(10), ui.Sx(10));
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.UpperLeft;

            ui.BuildLabel(region, "You could talk about:", 13, FontStyles.Bold, new Color(0.8f, 0.85f, 0.9f), TextAlignmentOptions.TopLeft);

            pointsText = ui.BuildLabel(region, "", 14, FontStyles.Normal, Color.white, TextAlignmentOptions.TopLeft);
            pointsText.enableWordWrapping = true;
            pointsText.enableAutoSizing = true;
            pointsText.fontSizeMin = ui.Sx(9);
            pointsText.fontSizeMax = ui.Sx(14);
            var ple = pointsText.gameObject.AddComponent<LayoutElement>();
            ple.flexibleHeight = 1f;

            // Done ends the run and moves the subject to the questionnaire. It is
            // hidden until the agent wraps up the conversation (the user said
            // goodbye → NotifyConversationOver), so it isn't offered from the
            // start; UpdateUiVisibility gates it on conversationWrappedUp. Distinct
            // from the operator's red End Run button.
            doneButton = ui.BuildButton(region, "Done", kGreen, 16, OnDonePressed);
            var dle = doneButton.GetComponent<LayoutElement>();
            int dh = ui.Sx(40);
            dle.minHeight = dh; dle.preferredHeight = dh; dle.flexibleHeight = 0f;
        }

        void BuildTaskGrid(RectTransform parent, float rootW, float rootH) {
            const float gridFracW = 0.72f;
            var region = ui.BuildAnchoredRegion(parent, "TaskGrid", new Vector2(0f, 0f), new Vector2(gridFracW, 0.16f), ui.Sx(6));
            taskGridGo = region.gameObject;
            var grid = region.gameObject.AddComponent<GridLayoutGroup>();
            grid.spacing = new Vector2(ui.Sx(4), ui.Sx(4));
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            int cols = 5;
            grid.constraintCount = cols;
            float regionW = Mathf.Max(1f, rootW * gridFracW - 2f * ui.Sx(6));
            float cellW = (regionW - (cols - 1) * ui.Sx(4)) / cols;
            float cellH = Mathf.Max(ui.Sx(22), rootH * 0.16f * 0.42f);
            grid.cellSize = new Vector2(cellW, cellH);
            for (int i = 0; i < kTaskLabels.Length; i++) {
                int idx = i;
                ui.BuildButton(region, kTaskLabels[i], kTaskBtn, 14, () => DebugStartTask(idx));
            }
        }

        void BuildLogPanel(RectTransform parent) {
            var panel = ui.BuildAnchoredRegion(parent, "Log", new Vector2(0.74f, 0f), new Vector2(1f, 0.4f), ui.Sx(6));
            logPanelGo = panel.gameObject;
            panel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
            logText = ui.BuildLabel(panel, "", 11, FontStyles.Normal, new Color(0.9f, 0.9f, 0.9f));
            logText.enableWordWrapping = false;
            logText.overflowMode = TextOverflowModes.Truncate;
            logText.alignment = TextAlignmentOptions.BottomLeft;
            QoeUI.StretchToParent((RectTransform)logText.transform);
            var rt = (RectTransform)logText.transform;
            rt.offsetMin = new Vector2(ui.Sx(6), ui.Sx(6));
            rt.offsetMax = new Vector2(-ui.Sx(6), -ui.Sx(6));
        }

        public void ConnectManual() {
            if (ws != null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.Connecting)) return;
            ClearError();
            SetHud($"Connecting to {WsDeviceUrl}…");
            _ = ConnectWs();
            if (ratingClient != null && !ratingClient.IsOpen) ratingClient.OpenWs(serverHost, serverPort);
        }

        public void DisconnectManual() {
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            StudyControls.conversationGateOpen = true; // don't leave the scene mic-locked
            CloseWsIntentional();
            TransitionPhase(DevicePhase.Idle);
            SetHud("Disconnected — press Connect");
        }

        public void SendReadyManual() {
            if (phase != DevicePhase.TaskReceived) return;
            TransitionPhase(DevicePhase.LoadingTask);
            SetHud($"Starting task '{activeLabel}'…");
            StartCoroutine(TeleportThenStart());
        }

        IEnumerator TeleportThenStart() {
            isDebugRun = false;
            TeleportToTask(activeTaskIndex);
            yield return null; // let rig move + camera-follower settle
            // Show the briefing and wait for Start. The `ready` handshake is NOT
            // sent here: it tells the operator to apply the netem condition and
            // begin its condition clock, which must line up with the device's own
            // timed window — and that window doesn't open until the subject has
            // read the context and pressed Start. So `ready` is sent in
            // OnStartPressed, not before the subject has even started reading.
            EnterBriefing();
        }

        // Place the subject in the briefing phase: conversation gated closed, the
        // per-task context shown on the HUD, timer not yet running. Shared by the
        // real WS path and the debug Task buttons so both behave identically.
        // OnStartPressed begins the actual timed run.
        void EnterBriefing() {
            StudyControls.conversationGateOpen = false;
            if (briefingText != null) briefingText.text = BriefingFor(activeTaskIndex);
            TransitionPhase(DevicePhase.Briefing);
            QoeLog.Event("task", $"briefing shown for '{activeLabel}' — waiting for Start");
        }

        // Start button: the subject has read the context and is ready to talk.
        // Opens the conversation gate and starts the timed measurement window. For
        // a real run this is also where we send `ready` (and close the WS), so the
        // operator's condition/netem clock starts together with the device timer.
        public void OnStartPressed() {
            if (phase != DevicePhase.Briefing) return;
            StudyControls.conversationGateOpen = true;
            conversationWrappedUp = false; // Done button hidden until the agent wraps up
            if (pointsText != null) pointsText.text = TalkingPointsBlock(activeTaskIndex);
            if (!isDebugRun) {
                QoeLog.Event("ws", $"sending ready for run {activeRunId}");
                SendJson(new { type = WsType.Ready });
                CloseWsIntentional();
                if (ratingClient != null) ratingClient.CloseWs();
            }
            QoeLog.Event("task", $"Start pressed — conversation open, timing '{activeLabel}'");
            TransitionPhase(DevicePhase.RunningTask);
            if (runCo != null) { StopCoroutine(runCo); }
            runCo = StartCoroutine(RunTaskThenEnd());
        }

        // Debug Task buttons run the SAME local lifecycle as a real task —
        // teleport, RunningTask phase, timer, mic indicator, End-Run button,
        // teleport-to-neutral on end — so what you see while debugging matches a
        // real run. The only things it skips are the operator-only bits: the
        // WebSocket ready/start handshake and the /end-condition POST. Uses
        // debugRunDurationS instead of the operator's max_condition_duration_s.
        public void DebugStartTask(int taskIndex) {
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            isDebugRun     = true;
            activeTaskIndex = taskIndex;
            activeLabel    = (taskIndex >= 0 && taskIndex < kTaskLabels.Length) ? kTaskLabels[taskIndex] : $"Task {taskIndex}";
            activeSid      = null;
            maxDurationS   = Mathf.Max(1, debugRunDurationS);
            QoeLog.Event("task", $"DEBUG run start: '{activeLabel}' index={taskIndex} duration={maxDurationS}s");
            TeleportToTask(taskIndex);
            // Same as a real run: show the briefing and gate the conversation
            // until Start is pressed (OnStartPressed begins the timer).
            EnterBriefing();
        }

        void Update() {
#if !UNITY_WEBGL || UNITY_EDITOR
            ws?.DispatchMessageQueue(); // NativeWebSocket requires manual pump on non-WebGL
#endif
            while (mainQ.TryDequeue(out var a)) a();
            UpdateMicDot();
        }

        void UpdateMicDot() {
            if (micDot == null) return;
            if (micHandler == null) micHandler = FindObjectOfType<MicrophoneHandler>();
            bool recording = micHandler != null && micHandler.IsRecording;
            var want = recording ? kMicRecording : kMicIdle;
            if (micDot.color != want) micDot.color = want;
            if (micLabel != null) {
                string txt = recording ? "REC" : "MIC";
                if (micLabel.text != txt) micLabel.text = txt;
                if (micLabel.color != want) micLabel.color = want;
            }
        }

        async void OnDestroy() {
            if (ws != null) await ws.Close();
        }

        void OnUnityLog(string msg, string stack, LogType type) {
            if (!msg.StartsWith("[Qoe]")) return;
            var line = type == LogType.Warning ? $"<color=yellow>{msg}</color>"
                     : type == LogType.Error   ? $"<color=red>{msg}</color>"
                     : msg;
            if (logLines.Count >= logMaxLines) logLines.Dequeue();
            logLines.Enqueue(line);
            if (logText != null) logText.text = string.Join("\n", logLines);
        }

        async System.Threading.Tasks.Task ConnectWs() {
            if (ws != null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.Connecting)) return;
            wsClosedByUs = false;
            isConnecting = true;
            UpdateButtonStates();
            QoeLog.Event("ws", $"connecting to {WsDeviceUrl}");
            ws = new WebSocket(WsDeviceUrl);
            connectTimeoutCo = StartCoroutine(ConnectTimeoutRoutine());

            ws.OnOpen += () => mainQ.Enqueue(() => {
                isConnecting = false;
                if (connectTimeoutCo != null) { StopCoroutine(connectTimeoutCo); connectTimeoutCo = null; }
                QoeLog.Event("ws", "connected");
                ClearError();
                SetHud("Connected — waiting for task...");
                SendHello();
                UpdateButtonStates();
            });

            ws.OnMessage += (bytes) => {
                if (bytes == null || bytes.Length == 0) return;
                var raw = Encoding.UTF8.GetString(bytes);
                mainQ.Enqueue(() => HandleWsMessage(raw));
            };

            ws.OnError += err => mainQ.Enqueue(() => {
                QoeLog.Err("ws", err);
                SetHud($"WS error: {err}");
                SetError("Connection error. Please wait for the operator.");
            });

            ws.OnClose += code => mainQ.Enqueue(() => {
                isConnecting = false;
                if (!wsClosedByUs) {
                    QoeLog.Warn("ws", $"unexpected close (code {code})");
                    SetHud(phase == DevicePhase.Idle
                        ? "Connection failed — press Connect to retry"
                        : $"Connection lost (code {code})");
                    SetError(phase == DevicePhase.Idle
                        ? "Could not reach the server. Please wait for the operator."
                        : "Connection lost. Please wait for the operator.");
                }
                UpdateButtonStates();
            });

            await ws.Connect();
        }

        IEnumerator ConnectTimeoutRoutine() {
            yield return new WaitForSeconds(ConnectTimeoutS);
            if (!isConnecting) yield break;
            QoeLog.Warn("ws", $"Connect timed out after {ConnectTimeoutS}s");
            isConnecting = false;
            SetHud("Connection timed out — press Connect to retry");
            SetError("Could not reach the server. Please wait for the operator.");
            UpdateButtonStates();
            connectTimeoutCo = null;
        }

        async void CloseWsIntentional() {
            if (ws == null) return;
            wsClosedByUs = true;
            await ws.Close();
        }

        async void SendJson(object obj) {
            if (ws == null || ws.State != WebSocketState.Open) {
                QoeLog.Warn("ws", $"SendJson called while WS not open (state={ws?.State})"); return;
            }
            await ws.SendText(JsonConvert.SerializeObject(obj));
        }

        void SendHello() {
            SendJson(new { type = WsType.Hello, data = new { kind = deviceKind, name = deviceName } });
        }

        void HandleWsMessage(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return;
            WsEnvelope env;
            try { env = JsonConvert.DeserializeObject<WsEnvelope>(raw); }
            catch (Exception e) { QoeLog.Err("ws", $"JSON parse error: {e.Message}  raw={raw}"); return; }
            if (env == null) return;

            switch (env.type) {
                case WsType.Rejected:
                    var reason = env.data?["reason"]?.ToString() ?? "unknown";
                    QoeLog.Warn("ws", $"Server REJECTED us: {reason}");
                    SetHud($"Rejected by server: {reason}");
                    break;
                case WsType.StartTask:
                    OnStartTask(env.data);
                    break;
                default:
                    QoeLog.Warn("ws", $"Unhandled message type: '{env.type}'");
                    break;
            }
        }

        void OnStartTask(JObject data) {
            if (data == null) { QoeLog.Warn("task", "start_task with null data — ignoring"); return; }
            activeSid    = data["session_id"]?.ToString();
            activeRunId  = data["condition_run_id"]?.ToObject<int>() ?? 0;
            activeLabel  = data["label"]?.ToString() ?? "?";
            maxDurationS = data["max_condition_duration_s"]?.ToObject<int>() ?? 60;

            // task_number is null for training runs, 1-based otherwise.
            int? taskNumber = data["task_number"]?.Type == JTokenType.Null
                ? (int?)null
                : data["task_number"]?.ToObject<int?>();
            activeTaskIndex = taskNumber ?? 0;

            string taskDesc = taskNumber.HasValue ? $"task_number={taskNumber.Value}" : "training (task_number=null)";
            QoeLog.Event("task", $"start_task: label='{activeLabel}' {taskDesc} duration={maxDurationS}s run={activeRunId}");
            if (string.IsNullOrEmpty(activeSid)) QoeLog.Warn("task", "session_id is null/empty in start_task payload");
            if (activeTaskIndex < 0 || activeTaskIndex >= taskSpawnPoints.Length)
                QoeLog.Warn("task", $"task index {activeTaskIndex} out of range for taskSpawnPoints[{taskSpawnPoints.Length}] — Ready will fail to teleport");

            TransitionPhase(DevicePhase.TaskReceived);
            SetHud($"Task received ('{activeLabel}') — press Ready to start");
        }

        IEnumerator RunTaskThenEnd() {
            QoeLog.Event("task", $"task started — label='{activeLabel}' maxDuration={maxDurationS}s");
            float t = 0; int lastWhole = -1;
            while (t < maxDurationS) {
                t += Time.deltaTime;
                int remaining = Mathf.CeilToInt(maxDurationS - t);
                if (remaining != lastWhole) {
                    SetTimer(remaining);
                    SetHud($"Running '{activeLabel}': {remaining}s");
                    lastWhole = remaining;
                }
                yield return null;
            }
            // Natural timer expiry. Clear runCo first so FinishRun doesn't try to
            // StopCoroutine the coroutine it's being called from.
            runCo = null;
            FinishRun("timer expired");
        }

        // Single end-of-run path shared by every way a run can end: timer expiry,
        // the operator End Run button, the agent wrapping up the conversation
        // (NotifyConversationOver), and the subject's Done button. Idempotent via
        // the RunningTask phase guard, so a conversation-over + timer race can't
        // double-fire /end-condition. The timer is purely an upper bound now — any
        // of the other reasons can end the round earlier so the subject goes
        // straight to rating instead of waiting it out.
        void FinishRun(string reason) {
            if (phase != DevicePhase.RunningTask) return;
            QoeLog.Event("task", $"finishing run {activeRunId} '{activeLabel}' — reason: {reason}");
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            SetTimer(0);
            EndConversationCleanup();
            TeleportToNeutral();
            if (isDebugRun) {
                SetHud($"Debug run ended ({reason})");
                TransitionPhase(DevicePhase.Idle);
            } else {
                SetHud($"Run ended ({reason}) — calling /end-condition…");
                StartCoroutine(PostEndCondition(activeSid));
            }
        }

        // The agent appended its end-of-conversation marker (user said goodbye)
        // and the goodbye clip has finished. Rather than end the run outright,
        // this REVEALS the Done button so the subject taps it to proceed to the
        // questionnaire on their own beat — the conversation has reached a natural
        // close, but we don't yank them out mid-moment. Static entry point so the
        // audio pipelines can reach the live client without a reference.
        public static void NotifyConversationOver() {
            if (instance == null) return;
            if (instance.phase != DevicePhase.RunningTask) return;
            if (instance.conversationWrappedUp) return; // already revealed
            instance.conversationWrappedUp = true;
            QoeLog.Event("task", $"agent wrapped up '{instance.activeLabel}' — Done button revealed");
            instance.SetHud("Conversation finished — press Done to continue");
            instance.UpdateUiVisibility();
            instance.UpdateButtonStates();
        }

        // Subject-facing Done button: ends the run and moves to the questionnaire.
        // Only shown after the agent has wrapped up (conversationWrappedUp). The
        // timer remains the backstop if the conversation never reaches a close.
        public void OnDonePressed() {
            if (phase != DevicePhase.RunningTask) return;
            FinishRun("subject pressed Done");
        }

        void SetTimer(int remainingS) {
            if (timerText == null) return;
            remainingS = Mathf.Max(0, remainingS);
            timerText.text = $"{remainingS / 60:0}:{remainingS % 60:00}";
        }

        public void EndRunEarly() {
            FinishRun("operator ended early");
        }

        // Hard-stop the conversation at the end of a run (timer expiry or End Run),
        // before teleporting the subject to neutral. Three things, so each trial
        // has a clean boundary: (1) cut every agent's audio so a clip still playing
        // — likely under high latency — doesn't keep talking from an empty spawn;
        // (2) reset the mic/thinking pipeline so a late response can't flip state
        // into the next run; (3) refresh the backend scene to wipe conversation
        // history so a re-run of the same agent starts fresh and any in-flight
        // LLM/TTS reply is discarded. Same path for debug and real runs.
        void EndConversationCleanup() {
            // Close the conversation gate so the mic is dead the instant the run
            // ends — covers the gap between end-of-run and the next briefing.
            StudyControls.conversationGateOpen = false;
            LLMAgents.AgentSelectionController.StopAllAgents();
            TrainingSceneController.StopAudio();
            if (StudyControls.instance != null) StudyControls.instance.ResetConversationState();
            if (activeTaskIndex >= 0 && activeTaskIndex < kTaskBackendScenes.Length) {
                string backendScene = kTaskBackendScenes[activeTaskIndex];
                ServerInterface.RefreshScene(backendScene);
                QoeLog.Event("task", $"end-of-run cleanup: stopped agents, reset mic, refreshed backend scene '{backendScene}'");
            } else {
                QoeLog.Event("task", "end-of-run cleanup: stopped agents, reset mic (no backend scene to refresh)");
            }
        }

        void TeleportToTask(int taskIndex) {
            if (playerTransform == null) {
                QoeLog.Warn("task", "playerTransform not assigned — cannot teleport");
                SetHud("playerTransform not set");
                return;
            }
            if (taskIndex < 0 || taskIndex >= taskSpawnPoints.Length || taskSpawnPoints[taskIndex] == null) {
                QoeLog.Warn("task", $"spawnPoint for index {taskIndex} not assigned");
                SetHud($"spawn point for index {taskIndex} not set");
                return;
            }
            ActivateOnlySceneFor(taskIndex);
            var spawn = taskSpawnPoints[taskIndex];
            playerTransform.SetPositionAndRotation(spawn.position, Quaternion.LookRotation(spawn.right));
            string backendScene = kTaskBackendScenes[taskIndex];
            ServerInterface.RefreshScene(backendScene);
            QoeLog.Event("task", $"teleported to {kTaskLabels[taskIndex]} (backend scene '{backendScene}')");
            SetHud($"Teleported to {kTaskLabels[taskIndex]}");
        }

        // Performance: keep only the scene root the player is entering active.
        // No-op unless sceneRoots is assigned in the Inspector. Disabling the
        // other three roots drops their renderers (and the ~70M-triangle total)
        // from the frame. The agent pipeline is unaffected — the active scene's
        // ActivationZone is what selects which agent prompt/voice answers.
        void ActivateOnlySceneFor(int taskIndex) {
            if (sceneRoots == null) return;
            int want = (taskIndex >= 0 && taskIndex < kTaskSceneRoot.Length) ? kTaskSceneRoot[taskIndex] : -1;
            bool anyAssigned = false;
            for (int i = 0; i < sceneRoots.Length; i++) {
                if (sceneRoots[i] == null) continue;
                anyAssigned = true;
                bool on = i == want;
                if (sceneRoots[i].activeSelf != on) sceneRoots[i].SetActive(on);
            }
            if (anyAssigned && want >= 0)
                QoeLog.Event("perf", $"scene root {want} active; others culled");
        }

        void TeleportToNeutral() {
            if (playerTransform == null) {
                QoeLog.Warn("task", "playerTransform not assigned — cannot teleport to neutral");
                return;
            }
            if (neutralPoint != null)
                playerTransform.SetPositionAndRotation(neutralPoint.position, neutralPoint.rotation);
            else
                playerTransform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            QoeLog.Event("task", "teleported to neutral point");
        }

        public void AbandonRun() {
            if (phase != DevicePhase.RunningTask && phase != DevicePhase.TaskReceived && phase != DevicePhase.LoadingTask && phase != DevicePhase.Briefing) return;
            QoeLog.Warn("task", $"abandon run {activeRunId} '{activeLabel}' phase={phase} — /end-condition NOT sent");
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            StudyControls.conversationGateOpen = true; // don't leave the scene mic-locked
            TeleportToNeutral();
            SetHud("Run abandoned locally");
            TransitionPhase(DevicePhase.Idle);
        }

        IEnumerator PostEndCondition(string sid) {
            var url = $"{HttpBase}/end-condition";
            var body = JsonConvert.SerializeObject(new { sid });
            const int maxAttempts = 5;
            const float retryDelay = 3f;

            for (int attempt = 1; attempt <= maxAttempts; attempt++) {
                using (var req = MakePostJson(url, body)) {
                    yield return req.SendWebRequest();
                    bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode >= 200 && req.responseCode < 300;
                    string respBody = req.downloadHandler?.text ?? "";

                    if (ok) {
                        bool ignored = TryParseIgnored(respBody, out var reason);
                        if (ignored) {
                            QoeLog.Warn("http", $"/end-condition ignored: {reason} — session over");
                            SetHud($"end-condition ignored ({reason}) — session over");
                            TransitionPhase(DevicePhase.Idle);
                        } else {
                            QoeLog.Event("http", "/end-condition acked — reconnecting");
                            SetHud("end-condition acked — reconnecting...");
                            TransitionPhase(DevicePhase.Idle);
                            _ = ConnectWs();
                            if (ratingClient != null) ratingClient.OpenWs(serverHost, serverPort);
                        }
                        yield break;
                    }

                    QoeLog.Warn("http", $"/end-condition attempt {attempt}/{maxAttempts} failed: HTTP {req.responseCode} {req.error}");
                    if (attempt < maxAttempts) {
                        QoeLog.Event("http", $"Waiting {retryDelay}s before retry…");
                        SetHud($"end-condition failed (attempt {attempt}/{maxAttempts}), retrying in {retryDelay}s…");
                    }
                }
                if (attempt < maxAttempts) yield return new WaitForSeconds(retryDelay);
            }

            QoeLog.Err("http", $"/end-condition gave up after {maxAttempts} attempts — going Idle");
            SetHud("end-condition gave up after retries — abandoning run");
            TransitionPhase(DevicePhase.Idle);
        }

        static bool TryParseIgnored(string respBody, out string reason) {
            reason = null;
            try {
                var j = JObject.Parse(respBody);
                if (j["ignored"]?.ToObject<bool>() == true) {
                    reason = j["reason"]?.ToString() ?? "unspecified";
                    return true;
                }
            } catch { }
            return false;
        }

        static UnityWebRequest MakePostJson(string url, string body) {
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            // req.insecureHttpOption = UnityWebRequest.InsecureHttpOption.AlwaysAllowed;
            return req;
        }

        void TransitionPhase(DevicePhase next) {
            if (phase == next) return;
            QoeLog.Event("phase", $"{phase} → {next}");
            phase = next;
            UpdateButtonStates();
            UpdateUiVisibility();
        }

        void UpdateButtonStates() {
            bool wsOpen        = ws != null && ws.State == WebSocketState.Open;
            bool canDisconnect = wsOpen && (phase == DevicePhase.Idle || phase == DevicePhase.TaskReceived);
            SetBtn(connectButton,     phase == DevicePhase.Idle && !isConnecting && !wsOpen);
            SetBtn(disconnectButton,  canDisconnect);
            SetBtn(sendReadyButton,   phase == DevicePhase.TaskReceived);
            SetBtn(startButton,       phase == DevicePhase.Briefing);
            SetBtn(doneButton,        phase == DevicePhase.RunningTask && conversationWrappedUp);
            SetBtn(endRunEarlyButton, phase == DevicePhase.RunningTask);
            RefreshConnStatus();
        }

        static void SetBtn(PressDownButton b, bool enabled) {
            if (b == null) return;
            b.interactable = enabled;
        }

        void SetHud(string s) { RefreshConnStatus(); }

        void RefreshConnStatus() {
            if (connStatusText == null) return;
            string s;
            if (IsWsOpen)          s = "Connected";
            else if (isConnecting) s = "Connecting…";
            else                   s = "Disconnected";
            connStatusText.text = s;
        }

        void SetError(string msg) {
            lastError = msg ?? "";
            if (errorText != null) errorText.text = lastError;
            UpdateUiVisibility();
        }

        void ClearError() => SetError("");
    }
}
