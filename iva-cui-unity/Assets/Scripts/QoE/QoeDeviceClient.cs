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
        // Operator console asks the device to upload the just-finished run's
        // conversation telemetry. The device answers with POST /telemetry. Mirrors
        // the qoe-lab XR simulator's request_telemetry → doUpload() flow.
        public const string RequestTelemetry = "request_telemetry";
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

        [Header("Pipecat WebRTC voice agent")]
        [Tooltip("The PipecatClient that connects to the Mac bot. Connect happens at " +
                 "briefing (per encounter), greeting releases on Start, disconnect on run-end.")]
        public PipecatClient pipecat;
        [Tooltip("The agent avatar's lip-sync AudioSource per task index, parallel to " +
                 "taskSpawnPoints: [0]=Training, [1..3]=City, [4..6]=Hotel, [7..9]=Museum. " +
                 "The agent voice plays through this so the avatar's OVR Lip Sync moves the mouth.")]
        public AudioSource[] taskAgentAudioSources = new AudioSource[10];
        [Tooltip("The agent's ActivationZone per task index (parallel to the above). " +
                 "Drives the look-at-player / attentive pose while talking. The zone is a " +
                 "sibling of the lip-sync AudioSource (not an ancestor), so it must be wired " +
                 "explicitly here rather than found by walking up from the AudioSource.")]
        public ActivationZone[] taskAgentZones = new ActivationZone[10];

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

        // agent_id sent to the Pipecat bot for EXPERIMENT tasks, indexed by
        // task_number (1..9). Index 0 is the training placeholder (training uses
        // kTrainingAgentIds instead). The bot looks this up to pick the agent's
        // persona + default voice (agents_config.py AGENTS dict).
        // task_number 1-3=City, 4-6=Hotel, 7-9=Museum.
        static readonly string[] kTaskAgentIds = { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9" };

        // ---- Training variants ----
        // The study can run several training conversations, all in the SAME physical
        // Alfred scene (placement index 0) with the SAME persona + voice, differing
        // only in the things to find out — so a subject who practices more than once
        // isn't bored by an identical warm-up. Selected by run_type=="training" +
        // task_number (1-based): variant = task_number - 1. The server has matching
        // agent_ids (t0/t0b/t0c) that share Alfred's persona but carry different
        // FACTS. Keep these three arrays parallel; add a variant by extending all.
        static readonly string[] kTrainingAgentIds = { "t0", "t0b", "t0c" };

        static readonly string[] kTrainingBriefings = {
            // Variant 1
            "Practice conversation with Alfred. This is how every conversation in the study works.\n\n" +
            "Each one gives you a short list of things to find out from the agent. Ask in your own words, in any order.\n\n" +
            "Speak naturally out loud, and feel free to interrupt at any time.\n\n" +
            "When you have everything, say goodbye, then press Done.\n\n" +
            "Try to notice how the conversation feels to have — not the place or the topic.\n\n" +
            "Press Start, then say hello to Alfred.",
            // Variant 2
            "Another quick practice with Alfred, to get comfortable before we begin.\n\n" +
            "As before: find out the things listed, in your own words and any order.\n\n" +
            "When you have everything, say goodbye, then press Done.\n\n" +
            "Press Start, then say hello to Alfred.",
            // Variant 3
            "One more practice chat with Alfred.\n\n" +
            "Find out the things listed, in your own words and any order.\n\n" +
            "When you have everything, say goodbye, then press Done.\n\n" +
            "Press Start, then say hello to Alfred.",
        };

        static readonly string[] kTrainingFindOuts = {
            // Variant 1
            "His favourite season: ____|Years he has worked here: ____|Time his shift started today: ____|Café he recommends nearby: ____",
            // Variant 2
            "His favourite hobby: ____|Number of languages he speaks: ____|Time the building opens: ____|His cat's name: ____",
            // Variant 3
            "His favourite drink: ____|Floor his office is on: ____|Time he takes his break: ____|Book he's reading: ____",
        };

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
        // The run's identity from start_task. run_type + 1-based task_number select
        // the CONTENT (briefing/slots/details/agent_id); activeTaskIndex is the
        // PLACEMENT slot into the flat 0..9 scene-resource arrays (spawn points,
        // audio sources, zones), which are physical and Inspector-wired. Training
        // (all variants) shares the one Alfred scene at placement 0; experiment
        // task N places at index N. See ResolveContent / activeTaskIndex below.
        bool isTraining;     // run_type == "training" (vs "experiment")
        int activeTaskNumber; // 1-based within its phase (training 1..k, experiment 1..n)
        int activeTaskIndex; // placement slot 0..9 into the scene-resource arrays
        bool isDebugRun;     // true when started by a debug Task button (no operator WS / no /end-condition)
        Coroutine runCo;
        Coroutine connectTimeoutCo;
        const float ConnectTimeoutS = 10f;

        readonly Queue<string> logLines = new();
        readonly ConcurrentQueue<Action> mainQ = new();

        readonly QoeUI ui = new();
        PressDownButton connectButton, disconnectButton, sendReadyButton, endRunEarlyButton, previewRatingButton, startButton;
        // Subject-facing Ready button placed at the bottom of the welcome panel
        // (non-debug). The right-side sendReadyButton is kept for debug. Both call
        // SendReadyManual.
        PressDownButton welcomeReadyButton;
        LazyCameraFollow canvasFollow;

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
        // NOTE: index 0 is a DEAD placeholder kept only to align this array with the
        // 0..9 placement layout (kTaskLabels / scene-resource arrays). Experiment
        // runs index this by task_number (1..9); training content lives in the
        // kTraining* arrays. Don't edit slot 0 expecting it to show — it never does.
        static readonly string[] kTaskBriefings = {
            "",                          // 0 unused (training → kTrainingBriefings)
            // 1–3 City (Shirts)
            "You're at your friend Sage's place, catching up.",
            "You're in a clothing store talking to Niko, the clerk. You're returning a shirt and also want some information.",
            "You've already returned your shirt to the clerk. Now you're at the back of the store with the manager, following up on the refund and asking about membership.",
            // 4–6 Hotel
            "You're checking in at the front desk of Hotel 333 with Hazel, under your reservation.",
            "You've checked into room 111. You run into Justin, a maintenance worker, on your floor — you have an issue to report and a few questions.",
            "You're at the hotel restaurant talking to Luka, the waiter. You're staying in room 111.",
            // 7–9 Museum
            "You're at the entrance of the Millennium Museum, planning your visit with Emma.",
            "You're at the Cyrus cylinder exhibit with Aleksander, the volunteer Emma mentioned.",
            "You're at the civil rights exhibit with Tammy, the volunteer.",
        };

        // Information slots the subject must find out from the agent this task —
        // shown in the briefing AND kept on the HUD during the run so they stay
        // visible while talking. Terse keyword/blank labels (not full questions) so
        // the subject phrases the utterance themselves. Every slot's answer is in
        // the agent's server-side FACTS block (agents_config.py), so the values are
        // consistent across participants. One string per task; slots split on '|'.
        static readonly string[] kTaskFindOuts = {
            "",                          // 0 unused (training → kTrainingFindOuts)
            // 1–3 City (Shirts)
            "Movie Sage saw last weekend: ____|Café Sage wants to try: ____|Day Sage is free to hang out: ____|Price Sage paid for their concert ticket: ____",
            "Today's closing time: ____|Price of the plain white T-shirt: ____|Days allowed for returns: ____|Floor of the fitting rooms: ____",
            "Days until the refund arrives: ____|Sunday opening time: ____|Member discount: ____ %|Name of the membership program: ____",
            // 4–6 Hotel
            "Breakfast start time: ____|Wi-Fi network name: ____|Checkout time: ____|Floor of the gym: ____",
            "What he's repairing right now: ____|When the pool reopens: ____|Floor of the ice machine: ____|Extension to reach maintenance: ____",
            "Today's special: ____|Its price: ____|Dessert he recommends: ____|Kitchen closing time: ____",
            // 7–9 Museum
            "Today's closing time: ____|Student ticket price: ____|Hall of the Cyrus cylinder: ____|Name of the volunteer at that exhibit: ____",
            "Material of the cylinder: ____|Year it dates from: ____|City it was found in: ____|Length of the audio guide: ____",
            "Time of today's guided talk: ____|Name of the photo collection on display: ____|Hall where the speech recording plays: ____|Year this exhibit opened: ____",
        };

        // Concrete details the subject "has" for this conversation — e.g. a
        // reservation number the receptionist may ask for. Arming the subject this
        // way means it's fine for an agent to ask in character (gives the subject
        // something real to say) without ever hanging them: they can just read it
        // off — it's the "something to give" half of the two-way task, balancing
        // the find-out slots. Shown in the briefing AND kept in the runtime panel.
        // Empty string = no details for that task (only Training). Lines split on '|'.
        static readonly string[] kTaskDetails = {
            "",                          // 0 unused (training has no details block)
            // 1–3 City (Shirts)
            "You just started a new job at \"Northlight Studio\"|You're free on Saturday",
            "Return confirmation code: 1 1 1 1|Item: a red shirt",
            "Your refund confirmation code: 1 1 1 1|You already returned a red shirt",
            // 4–6 Hotel
            "Reservation number: 2 4 6 8|Your name: Alex Taylor",
            "Your room: 111|The air conditioner in your room rattles",
            "Charge it to room 111|You're allergic to peanuts",
            // 7–9 Museum
            "You're a student",
            "Emma at reception sent you here",
            "You heard about the exhibit from Emma at the entrance",
        };

        // ---- Active-run content resolution ----
        // All four selectors key off the run identity (isTraining + activeTaskNumber)
        // rather than the placement index, so the training variants (which all share
        // placement slot 0) get their own briefing/slots/agent. Experiment tasks use
        // the flat arrays indexed by task_number (1..9). Variant index for training
        // is task_number-1.
        int ContentVariant => Mathf.Max(0, activeTaskNumber - 1);

        static string PickOr(string[] table, int i, string fallback) =>
            (table != null && i >= 0 && i < table.Length) ? table[i] : fallback;

        string ActiveBriefingIntro() => isTraining
            ? PickOr(kTrainingBriefings, ContentVariant, kTrainingBriefings[0])
            : PickOr(kTaskBriefings, activeTaskNumber,
                     "Press Start when you're ready to begin talking with the agent in front of you.");

        // agent_id sent to the Pipecat bot for this run (persona + default voice).
        string ActiveAgentId() => isTraining
            ? PickOr(kTrainingAgentIds, ContentVariant, "t0")
            : PickOr(kTaskAgentIds, activeTaskNumber, "t4");

        // The full briefing text (intro + details + find-out slots) for the active run.
        string ActiveBriefing() {
            var sb = new StringBuilder(ActiveBriefingIntro());
            string details = ActiveDetailsBlock();
            if (!string.IsNullOrEmpty(details))
                sb.Append("\n\nYour details (use these if asked):\n").Append(details);
            string points = ActiveFindOutsBlock();
            if (!string.IsNullOrEmpty(points))
                sb.Append("\n\nFind out:\n").Append(points);
            return sb.ToString();
        }

        // The find-out slots formatted as a bulleted block. Empty string if none.
        // Training has no "your details" block (only find-out slots).
        string ActiveFindOutsBlock() => isTraining
            ? BulletBlock(kTrainingFindOuts, ContentVariant)
            : BulletBlock(kTaskFindOuts, activeTaskNumber);

        // The subject's concrete details as a bulleted block. Empty string if none
        // (always empty for training).
        string ActiveDetailsBlock() => isTraining ? "" : BulletBlock(kTaskDetails, activeTaskNumber);

        static string BulletBlock(string[] table, int taskIndex) {
            if (table == null || taskIndex < 0 || taskIndex >= table.Length) return "";
            var raw = table[taskIndex];
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

        TMP_Text logText;
        TMP_Text timerText;
        // Mic level meter (top-left cluster): a fixed track with a fill that scales
        // with PipecatClient.MicLevel, so the participant can see Unity is hearing them.
        Image micMeterFill;
        RectTransform micMeterFillRT;
        TMP_Text briefingText;
        GameObject briefingGo;
        TMP_Text pointsText;
        TMP_Text detailsText;
        GameObject detailsHeaderGo;
        GameObject pointsGo;
        GameObject welcomeGo;
        TMP_Text welcomeText;
        PressDownButton doneButton;
        // True once the agent has wrapped up the conversation (user said goodbye).
        // The Done button stays hidden until this flips, so it isn't offered from
        // the start of the run. Reset at the start of each run.
        bool conversationWrappedUp;
        // True once the run has been going for kDoneFallbackS, regardless of whether
        // the agent reached its farewell. Gives the subject an off-ramp on tasks
        // where the conversation never naturally closes, without waiting out the
        // operator's full max-duration backstop. Reset at the start of each run.
        bool doneFallbackReached;
        // The Done button becomes available either way: the agent wrapped up, or the
        // fixed fallback delay has elapsed.
        bool DoneButtonAvailable => conversationWrappedUp || doneFallbackReached;
        // Seconds from Start before the Done button appears unconditionally. This is
        // the subject's own off-ramp and is independent of the operator's
        // max_condition_duration_s (which remains the hard upper bound on the run).
        const float kDoneFallbackS = 60f;
        TMP_Text connStatusText;
        TMP_Text errorText;
        GameObject topLeftGo;
        GameObject controlsGo;
        GameObject taskGridGo;
        GameObject logPanelGo;
        GameObject connStatusGo;
        GameObject errorGo;
        RectTransform centerRegion;
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
            BuildWelcomePanel(rootContainer);
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

            // Welcome / waiting panel at the neutral spawn, before any task runs.
            // Shown while Idle (waiting for the operator) and TaskReceived (Ready
            // showing), but not while a rating form is up, and not in debug (the
            // operator drives tasks directly from the grid there). Hidden the
            // moment a run begins.
            bool idleWaiting = phase == DevicePhase.Idle && IsWsOpen;
            bool showWelcome = !debugMode && !ratingVisible && (idleWaiting || taskReceived);
            if (welcomeGo != null) welcomeGo.SetActive(showWelcome);
            if (showWelcome && welcomeText != null)
                welcomeText.text = taskReceived ? kWelcomeReady : kWelcomeIdle;
            // The welcome-panel Ready button only when a task is waiting to start.
            SetActive(welcomeReadyButton, showWelcome && taskReceived);

            // Pre-conversation briefing panel — only while the subject is reading
            // the context, before they press Start. The debug Task buttons enter
            // Briefing too, so manual testing shows it exactly like a real run.
            if (briefingGo != null) briefingGo.SetActive(briefing);

            // Find-out slots panel — during the whole run. The Done button inside
            // it stays hidden until the agent wraps up OR the fixed fallback delay
            // elapses (DoneButtonAvailable).
            if (pointsGo != null) pointsGo.SetActive(running);
            SetActive(doneButton, running && DoneButtonAvailable);

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
            // Right-side Ready is debug-only now; the subject uses the Ready button
            // at the bottom of the welcome panel in non-debug.
            SetActive(sendReadyButton,     debugMode);
            // End Run is a debug/operator control only — the subject never sees it
            // in a real run. (The subject's own off-ramp is the Done button, which
            // appears after the agent wraps up.)
            SetActive(endRunEarlyButton,   debugMode);
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
            canvasFollow = root.GetComponent<LazyCameraFollow>();
            if (canvasFollow == null) canvasFollow = root.gameObject.AddComponent<LazyCameraFollow>();
            canvasFollow.distance = followDistance;
            if (followCameraTarget != null) canvasFollow.cam = followCameraTarget;
        }

        // Jump the HUD straight to its place in front of the camera after a
        // teleport, so it doesn't visibly slide in from the old location.
        void SnapHud() {
            if (canvasFollow != null) canvasFollow.SnapToTarget();
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
            topLeftGo = controlsGo = taskGridGo = logPanelGo = connStatusGo = errorGo = briefingGo = pointsGo = detailsHeaderGo = welcomeGo = null;
            timerText = null; centerRegion = null; briefingText = null; pointsText = null; detailsText = null; welcomeText = null;
            micMeterFill = null; micMeterFillRT = null;
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

            timerText = ui.BuildLabel(region, "0:00", 22, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
            timerText.enableWordWrapping = false;
            var tle = timerText.gameObject.AddComponent<LayoutElement>();
            tle.flexibleWidth = 0f; tle.minHeight = ui.Sx(28); tle.preferredHeight = ui.Sx(28);

            // Live mic level meter: a small label + a horizontal track whose fill
            // grows with the participant's voice level (PipecatClient.MicLevel),
            // updated each frame in Update(). Gives the subject confidence the Unity
            // frontend is actually hearing them, so silence reads as "speak up", not
            // "is this thing on?". Only visible while a task runs (the whole cluster
            // is gated on `running`), which is exactly when the mic is hot.
            ui.BuildLabel(region, "MIC", 12, FontStyles.Bold, new Color(0.7f, 0.74f, 0.8f), TextAlignmentOptions.Left)
              .gameObject.AddComponent<LayoutElement>().flexibleWidth = 0f;

            var trackGo = new GameObject("MicMeter", typeof(RectTransform), typeof(Image));
            trackGo.transform.SetParent(region, false);
            trackGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);
            var trackLe = trackGo.AddComponent<LayoutElement>();
            trackLe.minWidth = ui.Sx(90); trackLe.preferredWidth = ui.Sx(90); trackLe.flexibleWidth = 1f;
            trackLe.minHeight = ui.Sx(14); trackLe.preferredHeight = ui.Sx(14);

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(trackGo.transform, false);
            micMeterFill = fillGo.GetComponent<Image>();
            micMeterFill.color = new Color(0.36f, 0.78f, 0.4f); // green
            micMeterFillRT = (RectTransform)fillGo.transform;
            // Anchored left, fills vertically; width driven by anchorMax.x = level.
            micMeterFillRT.anchorMin = new Vector2(0f, 0f);
            micMeterFillRT.anchorMax = new Vector2(0f, 1f);
            micMeterFillRT.pivot = new Vector2(0f, 0.5f);
            micMeterFillRT.offsetMin = Vector2.zero; micMeterFillRT.offsetMax = Vector2.zero;
        }

        // Map the smoothed mic level (peak amplitude, ~0..0.3 for normal speech) to a
        // 0..1 bar width with a little gain so ordinary talking fills most of the
        // track, and tint it red→green so a barely-moving bar reads as "louder".
        void UpdateMicMeter() {
            if (micMeterFillRT == null) return;
            float level = pipecat != null ? pipecat.MicLevel : 0f;
            float norm = Mathf.Clamp01(level * 3.2f);
            micMeterFillRT.anchorMax = new Vector2(norm, 1f);
            micMeterFill.color = Color.Lerp(new Color(0.8f, 0.5f, 0.25f), new Color(0.36f, 0.78f, 0.4f), Mathf.Clamp01(norm * 1.5f));
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
            endRunEarlyButton = ui.BuildButton(region, "End Run",    kRed,   16, OnEndRunButton);
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

        // Subject-facing welcome shown at the neutral spawn before any task runs:
        // while Idle (waiting for the operator to start the next conversation) and
        // while a task has been received but not yet started (Ready showing). Plain
        // reassuring guidance so the subject isn't staring at a blank world; the
        // actual text is set per-phase in UpdateUiVisibility.
        void BuildWelcomePanel(RectTransform parent) {
            var region = ui.BuildAnchoredRegion(parent, "Welcome", new Vector2(0.12f, 0.3f), new Vector2(0.88f, 0.82f), ui.Sx(6));
            welcomeGo = region.gameObject;
            var bg = region.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);

            var vlg = region.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = ui.Sx(10);
            vlg.padding = new RectOffset(ui.Sx(18), ui.Sx(18), ui.Sx(16), ui.Sx(16));
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.MiddleCenter;

            welcomeText = ui.BuildLabel(region, "", 18, FontStyles.Normal, Color.white, TextAlignmentOptions.Center);
            welcomeText.enableWordWrapping = true;
            welcomeText.enableAutoSizing = true;
            welcomeText.fontSizeMin = ui.Sx(11);
            welcomeText.fontSizeMax = ui.Sx(18);
            var le = welcomeText.gameObject.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;

            // Ready button at the bottom of the welcome panel, centered under the
            // text — the subject's primary control. Shown only in the TaskReceived
            // phase (UpdateUiVisibility); calls the same SendReadyManual as the
            // debug right-side Ready.
            welcomeReadyButton = ui.BuildButton(region, "Ready", kGreen, 20, SendReadyManual);
            var rle = welcomeReadyButton.GetComponent<LayoutElement>();
            int rh = ui.Sx(46);
            rle.minHeight = rh; rle.preferredHeight = rh; rle.flexibleHeight = 0f;
        }

        const string kWelcomeIdle =
            "Please wait here — the next conversation will begin shortly.";
        const string kWelcomeReady =
            "Ready for the next conversation.\n\n" +
            "When you're ready to begin, press the Ready button.";

        // Pre-conversation briefing: a centered panel with the per-task context
        // text and a big Start button. Shown only during the Briefing phase (after
        // teleport, before the timed run). Pressing Start opens the conversation
        // gate and begins the timer — see OnStartPressed. Built separately from
        // the rating client's center region so neither clobbers the other.
        void BuildBriefingPanel(RectTransform parent) {
            // Near-full-canvas so even the long training briefing (format
            // explanation + find-out slots + details) has room.
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
            // overflowing. Left-aligned so the "• find-out slot" bullets read as a
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

        // During-run panel: the find-out slots reminder plus the
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

            // "Your details" section (e.g. reservation number) — shown first so
            // it's right there if the agent asks. Header + text are hidden per-task
            // when that task has no details (OnStartPressed sets visibility).
            detailsHeaderGo = ui.BuildLabel(region, "Your details (use if asked):", 13, FontStyles.Bold, new Color(0.95f, 0.85f, 0.5f), TextAlignmentOptions.TopLeft).gameObject;

            detailsText = ui.BuildLabel(region, "", 14, FontStyles.Bold, new Color(1f, 0.95f, 0.75f), TextAlignmentOptions.TopLeft);
            detailsText.enableWordWrapping = true;
            detailsText.enableAutoSizing = true;
            detailsText.fontSizeMin = ui.Sx(9);
            detailsText.fontSizeMax = ui.Sx(14);
            detailsText.gameObject.AddComponent<LayoutElement>();

            ui.BuildLabel(region, "Find out:", 13, FontStyles.Bold, new Color(0.8f, 0.85f, 0.9f), TextAlignmentOptions.TopLeft);

            pointsText = ui.BuildLabel(region, "", 14, FontStyles.Normal, Color.white, TextAlignmentOptions.TopLeft);
            pointsText.enableWordWrapping = true;
            pointsText.enableAutoSizing = true;
            pointsText.fontSizeMin = ui.Sx(9);
            pointsText.fontSizeMax = ui.Sx(14);
            var ple = pointsText.gameObject.AddComponent<LayoutElement>();
            ple.flexibleHeight = 1f;

            // Done ends the run and moves the subject to the questionnaire. It is
            // hidden until either the agent wraps up the conversation (user said
            // goodbye → NotifyConversationOver) or the fixed fallback delay elapses
            // (kDoneFallbackS), so it isn't offered from the start; visibility is
            // gated on DoneButtonAvailable. Distinct from the operator's red End Run
            // button.
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
            // One button per training variant (Tr1..TrN → DebugStartTask(true, n))...
            for (int v = 0; v < kTrainingFindOuts.Length; v++) {
                int num = v + 1;
                ui.BuildButton(region, $"Tr{num}", kTaskBtn, 14, () => DebugStartTask(true, num));
            }
            // ...then one per experiment task (task_number 1..9 → DebugStartTask(false, n)).
            for (int i = 1; i < kTaskLabels.Length; i++) {
                int num = i;
                ui.BuildButton(region, kTaskLabels[i], kTaskBtn, 14, () => DebugStartTask(false, num));
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
            if (briefingText != null) briefingText.text = ActiveBriefing();
            TransitionPhase(DevicePhase.Briefing);
            // Pipecat: connect to this task's agent NOW and release its greeting so it
            // introduces itself WHILE the subject reads the brief. This hides the
            // connection + first-inference cold-start (cold TTS ~5s) off the timed
            // clock and warms the pipeline, so the subject's first real utterance gets
            // a fast reply. The mic stays muted during briefing (gated on
            // conversationGateOpen, still false) — so the greeting is one-way preamble,
            // NOT a measured turn. Start just opens the gate + timer (OnStartPressed).
            if (pipecat != null) {
                // Audio sink + zone are PLACEMENT (the physical avatar in the scene),
                // so they index by activeTaskIndex. agent_id is CONTENT (which persona
                // answers), so it comes from the run identity (training variant or
                // experiment task_number).
                AudioSource sink = (activeTaskIndex >= 0 && activeTaskIndex < taskAgentAudioSources.Length)
                    ? taskAgentAudioSources[activeTaskIndex] : null;
                ActivationZone zone = (activeTaskIndex >= 0 && activeTaskIndex < taskAgentZones.Length)
                    ? taskAgentZones[activeTaskIndex] : null;
                string agentId = ActiveAgentId();
                pipecat.Connect(sink, agentId, zone);
                pipecat.OpenConversation(); // release greeting now (held until DC opens)
            }
            QoeLog.Event("task", $"briefing shown for '{activeLabel}' — connecting agent + greeting, waiting for Start");
        }

        // Start button: the subject has read the context and is ready to talk.
        // Opens the conversation gate and starts the timed measurement window. For
        // a real run this is also where we send `ready` (and close the WS), so the
        // operator's condition/netem clock starts together with the device timer.
        public void OnStartPressed() {
            if (phase != DevicePhase.Briefing) return;
            StudyControls.conversationGateOpen = true;
            // Pipecat: greeting was already released at briefing; opening the gate here
            // just makes the mic hot (PipecatClient gates micTrack on conversationGateOpen).
            conversationWrappedUp = false; // Done hidden until the agent wraps up...
            doneFallbackReached = false;   // ...or the fixed fallback delay elapses
            if (pointsText != null) pointsText.text = ActiveFindOutsBlock();
            // Details section: populate and show only when this task has details.
            string details = ActiveDetailsBlock();
            bool hasDetails = !string.IsNullOrEmpty(details);
            if (detailsText != null) detailsText.text = details;
            if (detailsText != null) detailsText.gameObject.SetActive(hasDetails);
            if (detailsHeaderGo != null) detailsHeaderGo.SetActive(hasDetails);
            if (!isDebugRun) {
                QoeLog.Event("ws", $"sending ready for run {activeRunId}");
                SendJson(new { type = WsType.Ready });
                CloseWsIntentional();
                if (ratingClient != null) ratingClient.CloseWs();
            }
            QoeLog.Event("task", $"Start pressed — conversation open, timing '{activeLabel}'");
            // QoE telemetry: open the per-run telemetry envelope at the run t0 — the
            // single point shared by real and debug runs (debug==real). Captures the
            // run identity + the device clock base. Both audio pipelines push their
            // turn-records into this until FinishRun finalizes it.
            // task_number is now always ≥1 (the server numbers training runs too).
            // backend_scene is by placement (Training for the training scene, else
            // the experiment scene at this task_number).
            string backendSceneForRun = isTraining
                ? "Training"
                : ((activeTaskIndex >= 0 && activeTaskIndex < kTaskBackendScenes.Length) ? kTaskBackendScenes[activeTaskIndex] : "");
            QoeTurnLog.BeginRun(isDebugRun ? null : activeSid, activeRunId, isTraining, activeTaskNumber, activeTaskIndex,
                activeLabel, backendSceneForRun, isDebugRun, deviceKind, maxDurationS, Time.time);
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
        // training=true selects a training variant (1-based variantNumber into
        // kTraining* arrays, placement slot 0); training=false selects experiment
        // task number 1..9 (placement slot == task number). Mirrors what a real
        // start_task sets from run_type + task_number.
        public void DebugStartTask(bool training, int number) {
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            isDebugRun       = true;
            isTraining       = training;
            activeTaskNumber = Mathf.Max(1, number);
            activeTaskIndex  = training ? 0 : activeTaskNumber;
            activeLabel      = training
                ? $"Training {activeTaskNumber}"
                : ((activeTaskNumber >= 0 && activeTaskNumber < kTaskLabels.Length) ? kTaskLabels[activeTaskNumber] : $"Task {activeTaskNumber}");
            activeSid        = null;
            maxDurationS     = Mathf.Max(1, debugRunDurationS);
            QoeLog.Event("task", $"DEBUG run start: '{activeLabel}' training={training} task_number={activeTaskNumber} place={activeTaskIndex} duration={maxDurationS}s");
            TeleportToTask(activeTaskIndex);
            // Same as a real run: show the briefing and gate the conversation
            // until Start is pressed (OnStartPressed begins the timer).
            EnterBriefing();
        }

        bool lastPipecatConnected;

        void Update() {
#if !UNITY_WEBGL || UNITY_EDITOR
            ws?.DispatchMessageQueue(); // NativeWebSocket requires manual pump on non-WebGL
#endif
            while (mainQ.TryDequeue(out var a)) a();

            // Re-enable the Start button the moment the Pipecat connection comes up
            // during briefing (UpdateButtonStates otherwise only runs on transitions).
            if (pipecat != null && phase == DevicePhase.Briefing && pipecat.HasAgentSpoken != lastPipecatConnected) {
                lastPipecatConnected = pipecat.HasAgentSpoken;
                UpdateButtonStates();
            }

            // Drive the mic level meter while its cluster is visible (task running).
            if (topLeftGo != null && topLeftGo.activeInHierarchy) UpdateMicMeter();
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
                UpdateUiVisibility(); // show the "welcome / waiting" panel now we're connected
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
                case WsType.RequestTelemetry:
                    OnRequestTelemetry(env.data);
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

            // run_type selects the phase (training vs experiment); task_number is now
            // always ≥1, numbered 1-based WITHIN its phase. Back-compat: if run_type
            // is absent (older server), infer training from a null/missing task_number.
            string runType = data["run_type"]?.ToString();
            bool hasTaskNumber = data["task_number"] != null && data["task_number"].Type != JTokenType.Null;
            if (!string.IsNullOrEmpty(runType))
                isTraining = runType == "training";
            else
                isTraining = !hasTaskNumber; // legacy fallback
            activeTaskNumber = hasTaskNumber ? data["task_number"].ToObject<int>() : 1;
            if (activeTaskNumber < 1) activeTaskNumber = 1;

            // Placement slot into the flat 0..9 scene-resource arrays: training (all
            // variants) shares the one Alfred scene at slot 0; experiment task N
            // places at slot N (1..9).
            activeTaskIndex = isTraining ? 0 : activeTaskNumber;

            QoeLog.Event("task", $"start_task: label='{activeLabel}' run_type={(isTraining ? "training" : "experiment")} task_number={activeTaskNumber} place={activeTaskIndex} duration={maxDurationS}s run={activeRunId}");
            if (string.IsNullOrEmpty(activeSid)) QoeLog.Warn("task", "session_id is null/empty in start_task payload");
            if (activeTaskIndex < 0 || activeTaskIndex >= taskSpawnPoints.Length)
                QoeLog.Warn("task", $"placement index {activeTaskIndex} out of range for taskSpawnPoints[{taskSpawnPoints.Length}] — Ready will fail to teleport");

            TransitionPhase(DevicePhase.TaskReceived);
            SetHud($"Task received ('{activeLabel}') — press Ready to start");
        }

        // Operator console is asking for the just-finished run's conversation
        // telemetry. Mirrors the qoe-lab XR simulator: on request_telemetry the
        // device POSTs the run's telemetry to /telemetry. We answer only if the run
        // it names is the one we last finalized (the envelope is held by QoeTurnLog);
        // the body carries sid + condition_run_id + the turn-records.
        void OnRequestTelemetry(JObject data) {
            int reqRunId  = data?["condition_run_id"]?.ToObject<int>() ?? -1;
            string reqSid = data?["session_id"]?.ToString();
            QoeLog.Event("telemetry", $"request_telemetry for run {reqRunId}");
            if (!QoeTurnLog.LastFinalizedMatches(reqSid, reqRunId)) {
                QoeLog.Warn("telemetry", $"no finalized telemetry for run {reqRunId} (have {QoeTurnLog.LastFinalizedRunId}) — ignoring request");
                return;
            }
            string body = QoeTurnLog.GetLastEnvelopeJson();
            if (string.IsNullOrEmpty(body)) {
                QoeLog.Warn("telemetry", "finalized run has no sid (debug?) — nothing to POST");
                return;
            }
            StartCoroutine(PostTelemetry(reqRunId, body));
        }

        // POST the telemetry envelope to /telemetry. Best-effort with a couple of
        // retries — telemetry must never block the session. The server stores the
        // body verbatim; 409 means it's already stored (treat as done, don't retry);
        // an {ignored:true} ack (e.g. a training run the server drops) is also done.
        // The envelope is on disk regardless, so a give-up just means pull it off the
        // headset later.
        IEnumerator PostTelemetry(int runId, string body) {
            var url = $"{HttpBase}/telemetry";
            const int maxAttempts = 3;
            const float retryDelay = 3f;
            for (int attempt = 1; attempt <= maxAttempts; attempt++) {
                using (var req = MakePostJson(url, body)) {
                    yield return req.SendWebRequest();
                    bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode >= 200 && req.responseCode < 300;
                    if (ok) {
                        QoeLog.Event("telemetry", $"/telemetry uploaded for run {runId}");
                        yield break;
                    }
                    if (req.responseCode == 409) {
                        QoeLog.Event("telemetry", $"/telemetry run {runId} already stored (409) — done");
                        yield break;
                    }
                    QoeLog.Warn("telemetry", $"/telemetry attempt {attempt}/{maxAttempts} failed: HTTP {req.responseCode} {req.error}");
                }
                if (attempt < maxAttempts) yield return new WaitForSeconds(retryDelay);
            }
            QoeLog.Err("telemetry", $"/telemetry gave up for run {runId} — telemetry file remains on device");
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
                // Reveal the subject's Done button once the fixed fallback delay has
                // elapsed, even if the agent never reached its farewell. Independent
                // of maxDurationS (the operator's hard backstop). Fires once.
                if (!doneFallbackReached && t >= kDoneFallbackS) {
                    doneFallbackReached = true;
                    QoeLog.Event("task", $"Done button revealed by {kDoneFallbackS:0}s fallback for '{activeLabel}'");
                    UpdateUiVisibility();
                    UpdateButtonStates();
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
            // QoE telemetry: finalize the per-run envelope (stamp end reason + time,
            // write the JSON file on the device, keep it for an inbound
            // request_telemetry). Done before cleanup/teleport. Every turn-record is
            // already captured at its playback start, so nothing is in flight here.
            QoeTurnLog.FinishRun(reason, Time.time);
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
        // Shown after the agent wraps up or after the fixed fallback delay
        // (DoneButtonAvailable). The operator's max-duration timer remains the hard
        // backstop if the subject never presses it.
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

        // The End Run button is available across the whole task lifecycle in
        // non-debug, not just mid-run, so the operator can bail out at any point.
        // Route to the right action for the current phase so the button is never
        // dead: during a run → FinishRun; while briefing / task-received /
        // loading → AbandonRun (no run to /end-condition yet); otherwise no-op.
        public void OnEndRunButton() {
            if (phase == DevicePhase.RunningTask) {
                FinishRun("operator ended early");
            } else if (phase == DevicePhase.Briefing
                    || phase == DevicePhase.TaskReceived
                    || phase == DevicePhase.LoadingTask) {
                AbandonRun();
            }
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
            // Pipecat: disconnect from the agent. Per-encounter reconnect on the next
            // task gives each agent a fresh bot session/context (and its own voice),
            // and discards any in-flight late reply.
            if (pipecat != null) pipecat.Disconnect();
            QoeLog.Event("task", "end-of-run cleanup: stopped agents, reset conversation state, disconnected agent");
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
            SnapHud();
            // SEAM (Pipecat): no HTTP backend scene-refresh on teleport anymore.
            QoeLog.Event("task", $"teleported to {kTaskLabels[taskIndex]}");
            SetHud($"Teleported to {kTaskLabels[taskIndex]}");
        }

        [Tooltip("TEMP/DEBUG: when off, scene-root culling is disabled — ALL scene " +
                 "roots are kept active instead of culling the non-active ones. Used to " +
                 "test whether culling's activate-timing breaks some avatars' lip-sync " +
                 "init (OVRLipSyncContextMorphTarget.Start dies if its mesh is inactive " +
                 "at Start). Turn back on for the real study (perf).")]
        public bool enableSceneCulling = false;

        // Performance: keep only the scene root the player is entering active.
        // No-op unless sceneRoots is assigned in the Inspector. Disabling the
        // other three roots drops their renderers (and the ~70M-triangle total)
        // from the frame. The agent pipeline is unaffected — the active scene's
        // ActivationZone is what selects which agent prompt/voice answers.
        void ActivateOnlySceneFor(int taskIndex) {
            if (sceneRoots == null) return;
            // Culling disabled (debug): force every scene root active so no avatar's
            // lip-sync init runs while its mesh is inactive.
            if (!enableSceneCulling) {
                bool any = false;
                for (int i = 0; i < sceneRoots.Length; i++) {
                    if (sceneRoots[i] == null) continue;
                    any = true;
                    if (!sceneRoots[i].activeSelf) sceneRoots[i].SetActive(true);
                }
                if (any) QoeLog.Event("perf", "scene culling DISABLED — all scene roots active");
                return;
            }
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
            SnapHud();
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
            // Start is gated until the agent has audibly begun greeting (RTVI
            // bot-started-speaking) — not merely the socket being up or PCM buffering —
            // so the subject can't Start into dead air. If no pipecat, don't block.
            SetBtn(startButton,       phase == DevicePhase.Briefing && (pipecat == null || pipecat.HasAgentSpoken));
            SetBtn(doneButton,        phase == DevicePhase.RunningTask && DoneButtonAvailable);
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
