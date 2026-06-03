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

    public enum DevicePhase { Idle, LoadingTask, TaskReceived, RunningTask }

    public class QoeDeviceClient : MonoBehaviour {
        // ── Inspector config ─────────────────────────────────────────────
        [Header("Server")]
        public string serverHost = "192.168.1.50";
        public int serverPort = 8080;

        [Header("Device identity (sent in `hello`)")]
        public string deviceKind = "quest3-unity";
        public string deviceName = "Quest 3 (Unity)";

        [Header("UI")]
        [Tooltip("Empty RectTransform under a Canvas. HUD label + controls row + log panel + rating section are all built inside it at runtime.")]
        public RectTransform rootContainer;

        [Tooltip("If true, attach LazyCameraFollow to the canvas so it hovers in front of the player camera.")]
        public bool followCamera = true;
        [Tooltip("Distance (m) the canvas sits in front of the camera when followCamera is on.")]
        public float followDistance = 1.0f;
        [Tooltip("Camera tracked when followCamera is on. Falls back to Camera.main if null.")]
        public Transform followCameraTarget;

        [Header("Rating client (optional)")]
        // Leave null to disable on-headset ratings entirely. When set, its WS is
        // opened after each /end-condition reconnect and closed on Send Ready,
        // and its UI is built inside our rootContainer below the log panel.
        public QoeRatingClient ratingClient;

        [Header("Debug")]
        [Tooltip("When on, renders the log panel and the rating section's 'Debug preview' button. Turn off for subject-facing builds.")]
        public bool debugMode = true;

        [Header("Log panel")]
        public int logMaxLines = 12;

        [Header("Task teleport")]
        [Tooltip("Player root to teleport — whichever of 'XR Interaction Setup' or 'WASD Player' is active in the scene.")]
        public Transform playerTransform;
        [Tooltip("Spawn points indexed by task: [0]=Training (task_number null), [1]=Task 1, [2]=Task 2, [3]=Task 3. Assign in the inspector. start_task's task_number maps directly to this index (null→0).")]
        public Transform[] taskSpawnPoints = new Transform[4];
        [Tooltip("Where the player is sent when a run ends (timer expires, End Run, or abandon) to remove the stimuli — i.e. move them away from the agent during the rating/break phase. If unassigned, defaults to world origin (0,0,0).")]
        public Transform neutralPoint;

        [Header("Screen fade (VR comfort)")]
        [Tooltip("Fades the view to black before a teleport so the headset compositor reprojects black during the swap. Auto-created on this GameObject if left null.")]
        public ScreenFader fader;
        [Tooltip("Seconds for each fade-out / fade-in. ~0.3 is comfortable.")]
        public float fadeDuration = 0.3f;

        string HttpBase    => $"http://{serverHost}:{serverPort}";
        string WsDeviceUrl => $"ws://{serverHost}:{serverPort}/device";

        // ── Runtime state ────────────────────────────────────────────────
        WebSocket ws;
        bool wsClosedByUs;
        bool isConnecting;
        DevicePhase phase = DevicePhase.Idle;

        bool IsWsOpen => ws != null && ws.State == WebSocketState.Open;

        string activeSid;
        int activeRunId;
        string activeLabel;
        int maxDurationS;
        // Spawn index for the current run, resolved from start_task's task_number
        // (null/training → 0, N → N). Used by SendReadyManual to teleport.
        int activeTaskIndex;
        Coroutine runCo;
        Coroutine connectTimeoutCo;
        const float ConnectTimeoutS = 10f;

        readonly Queue<string> logLines = new();
        readonly ConcurrentQueue<Action> mainQ = new();

        // Code-built UI. References saved here for runtime updates and for
        // per-phase / per-mode visibility toggling (see UpdateUiVisibility).
        readonly QoeUI ui = new();
        PressDownButton connectButton, disconnectButton, sendReadyButton, endRunEarlyButton;
        // Training + Task 1–9 (the eventual 3 scenes × 3 agents). Only the first
        // few have spawn points / backend scenes wired today; the rest render as
        // debug buttons but TeleportToTask no-ops on them until they're assigned.
        static readonly string[] kTaskLabels = {
            "Training", "Task 1", "Task 2", "Task 3", "Task 4",
            "Task 5", "Task 6", "Task 7", "Task 8", "Task 9",
        };
        // Backend conversation scene to refresh per task (index matches kTaskLabels
        // / taskSpawnPoints). The backend keeps ONE global handler keyed by scene,
        // so we must refresh the right scene when teleporting or agent1/2/3 resolve
        // to the wrong scene's prompt + voice. Tasks 1–3 are Hotel agents; 4–9 are
        // unassigned (empty → no refresh) until City/Museum are merged in.
        static readonly string[] kTaskBackendScenes = {
            "Training", "Hotel", "Hotel", "Hotel", "",
            "", "", "", "", "",
        };

        // Palette (shared by controls + task grid + mic dot).
        static readonly Color kBlue  = new(0.16f, 0.5f,  0.95f);
        static readonly Color kGray  = new(0.55f, 0.55f, 0.6f);
        static readonly Color kGreen = new(0.2f,  0.7f,  0.35f);
        static readonly Color kRed   = new(0.8f,  0.35f, 0.25f);
        static readonly Color kTaskBtn = new(0.3f, 0.4f, 0.55f);
        static readonly Color kMicRecording = new(0.9f, 0.2f, 0.2f);
        static readonly Color kMicIdle      = new(0.32f, 0.32f, 0.38f);

        TMP_Text hudText;          // debug-only status line (top-center)
        TMP_Text logText;          // debug-only log (bottom-right)
        TMP_Text timerText;        // task countdown (top-left, both modes)
        Image    micDot;           // mic indicator next to the timer
        GameObject hudGo;          // top-center status (debug only)
        GameObject topLeftGo;      // timer + mic cluster
        GameObject controlsGo;     // connect/disconnect/ready/end cluster (top-right)
        GameObject taskGridGo;     // Training + Task 1–9 grid (bottom, debug only)
        GameObject logPanelGo;     // log window (bottom-right, debug only)
        RectTransform centerRegion;// rating UI + (later) pre-convo prompt
        MicrophoneHandler micHandler;

        // ── Lifecycle ────────────────────────────────────────────────────
        void OnEnable()  { Application.logMessageReceived += OnUnityLog; }
        void OnDisable() { Application.logMessageReceived -= OnUnityLog; }

        void Start() {
            QoeLog.Event("init", $"server={HttpBase} kind={deviceKind}");
            EnsureFader();
            BuildUi();
            SetHud("Ready — press Connect");
            UpdateButtonStates();
        }

        // The fade quad parents itself to the active camera at fade time, so the
        // fader can live on this (persistent shell) GameObject regardless of
        // which rig owns the view. Auto-created so no inspector wiring is needed.
        void EnsureFader() {
            if (fader == null) fader = GetComponent<ScreenFader>();
            if (fader == null) fader = gameObject.AddComponent<ScreenFader>();
        }

        // ── UI ───────────────────────────────────────────────────────────
        // Builds a corner-anchored HUD inside rootContainer (stretched to fill
        // the canvas). Regions are placed by fractional anchors so they scale
        // with the canvas. Layout (debug mode shows all of it):
        //
        //   ┌ TL: mic+timer ─┬ TC: status ─┬ TR: controls ┐
        //   │                              │  Connect      │
        //   │        CENTER                │  Disconnect   │
        //   │   (rating / pre-convo prompt)│  Ready        │
        //   │                              │  End Run      │
        //   ├ task grid (Training, 1–9) ───┴─ log window ──┤
        //   └──────────────────────────────────────────────┘
        //
        // Non-debug mode strips it to the essentials: mic+timer (during a task),
        // the single relevant control button top-right (Connect / Ready / End,
        // shown only when its action is valid), and the center rating form. No
        // status text, no log, no task grid, no titles/labels.
        void BuildUi() {
            if (rootContainer == null) {
                QoeLog.Err("ui", "rootContainer not assigned — cannot build device UI");
                return;
            }
            for (int i = rootContainer.childCount - 1; i >= 0; i--) {
                var child = rootContainer.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
            // Earlier builds (and edit-mode previews) stacked children with a
            // VerticalLayoutGroup on the root; the corner layout positions its
            // own children, so any inherited layout driver must go.
            StripComponent<VerticalLayoutGroup>(rootContainer.gameObject);
            StripComponent<HorizontalLayoutGroup>(rootContainer.gameObject);
            StripComponent<ContentSizeFitter>(rootContainer.gameObject);

            // Fill the canvas so fractional anchors map to the full panel.
            rootContainer.anchorMin = Vector2.zero; rootContainer.anchorMax = Vector2.one;
            rootContainer.offsetMin = Vector2.zero; rootContainer.offsetMax = Vector2.zero;

            Canvas.ForceUpdateCanvases();
            float rootW = rootContainer.rect.width;
            float rootH = rootContainer.rect.height;
            ui.scale = rootW > 0 ? Mathf.Clamp(rootW / 600f, 0.05f, 4f) : 1f;

            BuildTopLeftCluster(rootContainer);   // mic dot + timer
            BuildHud(rootContainer);              // status text (debug only)
            BuildControlsCluster(rootContainer);  // connect/disconnect/ready/end
            BuildCenterRegion(rootContainer);     // rating + (later) prompt
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

        // Drives per-region and per-button visibility for the two modes. Called
        // on every phase change, rating-form toggle, and at build time.
        //
        //   debug   → show everything; all four control buttons visible and
        //             dimmed/enabled per phase (UpdateButtonStates).
        //   normal  → mic+timer only during a task; exactly one control button
        //             (the valid one) top-right; center rating form when present;
        //             no status text, log, or task grid.
        void UpdateUiVisibility() {
            if (rootContainer == null) return;
            rootContainer.gameObject.SetActive(true);

            bool ratingVisible = ratingClient != null && ratingClient.IsFormVisible;
            bool running       = phase == DevicePhase.RunningTask;
            bool canConnect    = phase == DevicePhase.Idle && !isConnecting && !IsWsOpen;
            bool taskReceived  = phase == DevicePhase.TaskReceived;

            // Timer + mic: meaningful only during a task, in both modes.
            if (topLeftGo  != null) topLeftGo.SetActive(running);
            // Status / log / task grid: debug only.
            if (hudGo      != null) hudGo.SetActive(debugMode);
            if (logPanelGo != null) logPanelGo.SetActive(debugMode);
            if (taskGridGo != null) taskGridGo.SetActive(debugMode);

            // Controls. Debug shows all four (enabled state handled separately);
            // normal shows only the one button whose action is currently valid,
            // and hides Disconnect entirely.
            SetActive(connectButton,    debugMode || (canConnect   && !ratingVisible));
            SetActive(disconnectButton, debugMode);
            SetActive(sendReadyButton,  debugMode || (taskReceived && !ratingVisible));
            SetActive(endRunEarlyButton, debugMode || running);
            if (controlsGo != null) controlsGo.SetActive(debugMode || canConnect || taskReceived || running);
        }

        static void SetActive(PressDownButton b, bool on) {
            if (b != null) b.gameObject.SetActive(on);
        }

        static void StripComponent<T>(GameObject go) where T : Component {
            var c = go.GetComponent<T>();
            if (c == null) return;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
        }

        // Walks up from rootContainer to its Canvas root and attaches a
        // LazyCameraFollow so the whole HUD hovers in front of the player.
        // Toggle followCamera off if you want a stationary canvas (e.g. desktop
        // testing inside a fixed window). Skipped in edit mode so the editor
        // preview doesn't dirty the scene with a follower component.
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

        // ── Editor helpers (right-click the component header) ────────────
        // Three previews mirror the three runtime layouts:
        //   • Controls UI — what the operator sees in Idle/TaskReceived (debug
        //     adds the log panel; the rating section's status bar follows the
        //     same flag).
        //   • Rating UI   — what the subject sees while answering: builds the
        //     full HUD and populates the rating form with the debug preview,
        //     then applies subject-facing visibility (controls/HUD hidden).
        //   • Clear       — empties rootContainer.
        [ContextMenu("Device: build controls UI")]
        void Editor_BuildControlsUi() {
            BuildUi();
            SetHud("(edit-mode preview)");
            UpdateButtonStates();
        }

        [ContextMenu("Device: build rating UI")]
        void Editor_BuildRatingUi() {
            BuildUi();
            SetHud("(edit-mode preview)");
            UpdateButtonStates();
            if (ratingClient == null) {
                QoeLog.Warn("ui", "ratingClient not assigned — cannot preview rating UI");
                return;
            }
            ratingClient.LoadDebugPreview();
        }

        [ContextMenu("Device: clear UI")]
        void Editor_ClearUi() {
            if (rootContainer == null) return;
            for (int i = rootContainer.childCount - 1; i >= 0; i--) {
                var child = rootContainer.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
            hudGo = topLeftGo = controlsGo = taskGridGo = logPanelGo = null;
            timerText = null; micDot = null; centerRegion = null;
        }

        // ── Corner regions ─────────────────────────────────────────────────
        // Top-left: mic indicator dot + task countdown timer. Shown in BOTH
        // modes, but only while a task is running (UpdateUiVisibility gates it).
        void BuildTopLeftCluster(RectTransform parent) {
            var region = ui.BuildAnchoredRegion(parent, "TopLeft", new Vector2(0f, 0.84f), new Vector2(0.4f, 1f), ui.Sx(6));
            topLeftGo = region.gameObject;
            var hg = region.gameObject.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = ui.Sx(8);
            hg.childForceExpandWidth = false; hg.childForceExpandHeight = true;
            hg.childControlWidth = true; hg.childControlHeight = true;
            hg.childAlignment = TextAnchor.MiddleLeft;

            // Mic dot — a small square Image whose color tracks IsRecording.
            var dotGo = new GameObject("MicDot", typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(region, false);
            micDot = dotGo.GetComponent<Image>();
            micDot.color = kMicIdle;
            var dotLe = dotGo.AddComponent<LayoutElement>();
            int dot = ui.Sx(20);
            dotLe.minWidth = dot; dotLe.preferredWidth = dot; dotLe.flexibleWidth = 0;
            dotLe.minHeight = dot; dotLe.preferredHeight = dot; dotLe.flexibleHeight = 0;

            timerText = ui.BuildLabel(region, "", 22, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
            timerText.enableWordWrapping = false;
            var tle = timerText.gameObject.AddComponent<LayoutElement>();
            tle.flexibleWidth = 1f; tle.minHeight = ui.Sx(28); tle.preferredHeight = ui.Sx(28);
        }

        // Top-center: status text. Debug only — non-debug shows no text at all.
        void BuildHud(RectTransform parent) {
            var region = ui.BuildAnchoredRegion(parent, "Status", new Vector2(0.4f, 0.86f), new Vector2(0.72f, 1f), ui.Sx(4));
            hudText = ui.BuildLabel(region, "", 16, FontStyles.Bold, new Color(0.55f, 0.7f, 1f), TextAlignmentOptions.Top);
            hudText.enableWordWrapping = true;
            hudGo = hudText.gameObject;
            QoeUI.StretchToParent((RectTransform)hudText.transform);
        }

        // Top-right: the four control buttons stacked vertically. In debug all
        // four are present (enabled/dimmed per phase). In normal mode only the
        // single valid action is shown (UpdateUiVisibility), so the stack reads
        // as one button at the top-right.
        void BuildControlsCluster(RectTransform parent) {
            var region = ui.BuildAnchoredRegion(parent, "Controls", new Vector2(0.74f, 0.5f), new Vector2(1f, 1f), ui.Sx(6));
            controlsGo = region.gameObject;
            var vlg = region.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = ui.Sx(6);
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childAlignment = TextAnchor.UpperCenter;

            connectButton     = ui.BuildButton(region, "Connect",    kBlue,  20, ConnectManual);
            disconnectButton  = ui.BuildButton(region, "Disconnect", kGray,  20, DisconnectManual);
            sendReadyButton   = ui.BuildButton(region, "Ready",      kGreen, 20, SendReadyManual);
            endRunEarlyButton = ui.BuildButton(region, "End Run",    kRed,   20, EndRunEarly);
            int btnH = ui.Sx(40);
            foreach (var b in new[] { connectButton, disconnectButton, sendReadyButton, endRunEarlyButton }) {
                var le = b.GetComponent<LayoutElement>();
                le.minHeight = btnH; le.preferredHeight = btnH; le.flexibleHeight = 0;
            }
        }

        // Center: reserved for the rating form and (later) the pre-conversation
        // prompt/context. The rating client builds its section inside this rect.
        // The footprint depends on mode: debug keeps clear of the right control
        // column and the bottom task-grid/log strip, so it's a narrower box in
        // the middle-left; non-debug has none of that furniture, so it spreads
        // across nearly the whole panel (only the slim top band stays reserved
        // for the timer + the single top-right action button).
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

        // Bottom strip: Training + Task 1–9 teleport buttons as a grid. Debug
        // only. Each jumps the rig straight to that agent and refreshes the
        // backend scene — no WS / ready / end-condition. The grid auto-sizes its
        // cells to fit the panel width across kTaskLabels.Length entries.
        void BuildTaskGrid(RectTransform parent, float rootW, float rootH) {
            var region = ui.BuildAnchoredRegion(parent, "TaskGrid", new Vector2(0f, 0f), new Vector2(0.7f, 0.16f), ui.Sx(6));
            taskGridGo = region.gameObject;
            var grid = region.gameObject.AddComponent<GridLayoutGroup>();
            grid.spacing = new Vector2(ui.Sx(4), ui.Sx(4));
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            int cols = 5; // 10 task buttons → 5×2 grid
            grid.constraintCount = cols;
            float regionW = Mathf.Max(1f, rootW * 0.7f - 2f * ui.Sx(6));
            float cellW = (regionW - (cols - 1) * ui.Sx(4)) / cols;
            float cellH = Mathf.Max(ui.Sx(22), rootH * 0.16f * 0.42f);
            grid.cellSize = new Vector2(cellW, cellH);

            for (int i = 0; i < kTaskLabels.Length; i++) {
                int idx = i;
                ui.BuildButton(region, kTaskLabels[i], kTaskBtn, 14, () => TeleportToTask(idx));
            }
        }

        // Bottom-right: log window. Debug only.
        void BuildLogPanel(RectTransform parent) {
            var panel = ui.BuildAnchoredRegion(parent, "Log", new Vector2(0.7f, 0f), new Vector2(1f, 0.16f), ui.Sx(6));
            logPanelGo = panel.gameObject;
            panel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            logText = ui.BuildLabel(panel, "", 11, FontStyles.Normal, new Color(0.9f, 0.9f, 0.9f));
            logText.enableWordWrapping = false;
            logText.overflowMode = TextOverflowModes.Truncate;
            QoeUI.StretchToParent((RectTransform)logText.transform);
            var rt = (RectTransform)logText.transform;
            rt.offsetMin = new Vector2(ui.Sx(6), ui.Sx(6));
            rt.offsetMax = new Vector2(-ui.Sx(6), -ui.Sx(6));
        }

        // ── Button actions ────────────────────────────────────────────────
        public void ConnectManual() {
            if (ws != null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.Connecting)) return;
            SetHud($"Connecting to {WsDeviceUrl}…");
            _ = ConnectWs();
            // Open the rating WS too. If the operator console issued a
            // `request_rating` before we joined (mid-session reconnect after a
            // crash/disconnect), it'll re-fire on rating-WS reconnect and the
            // pending form lands without operator intervention.
            if (ratingClient != null && !ratingClient.IsOpen) ratingClient.OpenWs(serverHost, serverPort);
        }

        public void DisconnectManual() {
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
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

        // Per the device contract: teleport to the task_number's spawn (no scene
        // load), send `ready`, then close the WS and record locally for the run.
        IEnumerator TeleportThenStart() {
            TeleportToTask(activeTaskIndex);
            // One frame so the rig move + camera-follower retarget settle before
            // we tell the backend to apply netem and start the clock.
            yield return null;
            QoeLog.Event("ws", $"sending ready for run {activeRunId}");
            SendJson(new { type = WsType.Ready });
            CloseWsIntentional();
            if (ratingClient != null) ratingClient.CloseWs();
            TransitionPhase(DevicePhase.RunningTask);
            runCo = StartCoroutine(RunTaskThenEnd());
        }

        void Update() {
            // Upstream NativeWebSocket buffers callbacks until the main thread
            // pumps DispatchMessageQueue; without this, OnOpen/OnMessage/OnClose
            // never fire on Standalone/Android (the Meta fork dispatches itself).
#if !UNITY_WEBGL || UNITY_EDITOR
            ws?.DispatchMessageQueue();
#endif
            while (mainQ.TryDequeue(out var a)) a();
            UpdateMicDot();
        }

        // Mirror the mic state onto the indicator dot. Cheap enough to poll each
        // frame; only touches the Image when the color actually changes.
        void UpdateMicDot() {
            if (micDot == null) return;
            if (micHandler == null) micHandler = FindObjectOfType<MicrophoneHandler>();
            bool recording = micHandler != null && micHandler.IsRecording;
            var want = recording ? kMicRecording : kMicIdle;
            if (micDot.color != want) micDot.color = want;
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

        // ── WS ────────────────────────────────────────────────────────────
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
                SetHud("Connected — waiting for task...");
                SendHello();
                UpdateButtonStates();
            });

            ws.OnMessage += (bytes) => {
                // Upstream NativeWebSocket (endel) signature: full-buffer byte
                // array, no offset/length pair like the Meta XR SDK fork.
                if (bytes == null || bytes.Length == 0) return;
                var raw = Encoding.UTF8.GetString(bytes);
                mainQ.Enqueue(() => HandleWsMessage(raw));
            };

            ws.OnError += err => mainQ.Enqueue(() => {
                QoeLog.Err("ws", err);
                SetHud($"WS error: {err}");
            });

            ws.OnClose += code => mainQ.Enqueue(() => {
                isConnecting = false;
                if (!wsClosedByUs) {
                    QoeLog.Warn("ws", $"unexpected close (code {code})");
                    SetHud(phase == DevicePhase.Idle
                        ? "Connection failed — press Connect to retry"
                        : $"Connection lost (code {code})");
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
            var json = JsonConvert.SerializeObject(obj);
            await ws.SendText(json);
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

        // ── State machine ─────────────────────────────────────────────────
        void OnStartTask(JObject data) {
            if (data == null) { QoeLog.Warn("task", "start_task with null data — ignoring"); return; }
            activeSid    = data["session_id"]?.ToString();
            activeRunId  = data["condition_run_id"]?.ToObject<int>() ?? 0;
            activeLabel  = data["label"]?.ToString() ?? "?";
            maxDurationS = data["max_condition_duration_s"]?.ToObject<int>() ?? 60;

            // task_number: 1-based experiment task position, or null for training
            // runs (CONTRACT.md). The task sequence is fixed, so task_number maps
            // directly to a spawn index: null → 0 (Training), N → N.
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
                    SetTimer(remaining);                          // top-left, both modes
                    SetHud($"Running '{activeLabel}': {remaining}s"); // status, debug only
                    lastWhole = remaining;
                }
                yield return null;
            }
            QoeLog.Event("task", $"task finished after {maxDurationS}s — calling /end-condition");
            SetTimer(0);
            TeleportToNeutral();
            yield return PostEndCondition(activeSid);
            runCo = null;
        }

        // mm:ss countdown for the top-left timer. Kept tiny — the run loop only
        // calls this when the whole-second value changes.
        void SetTimer(int remainingS) {
            if (timerText == null) return;
            remainingS = Mathf.Max(0, remainingS);
            timerText.text = $"{remainingS / 60:0}:{remainingS % 60:00}";
        }

        public void EndRunEarly() {
            if (phase != DevicePhase.RunningTask) return;
            QoeLog.Event("task", $"end run early: run {activeRunId} '{activeLabel}'");
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            TeleportToNeutral();
            SetHud("Ending run early — calling /end-condition…");
            StartCoroutine(PostEndCondition(activeSid));
        }

        // ── Task teleport ─────────────────────────────────────────────────
        // Move the player rig to a task's spawn point and switch the backend to
        // that task's conversation scene. Used by both the debug task buttons
        // (manual, no networking) and the real WS path (SendReadyManual →
        // TeleportThenStart). taskIndex 0 = Training, 1–3 = Hotel agents.
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
            var spawn = taskSpawnPoints[taskIndex];
            playerTransform.SetPositionAndRotation(spawn.position, Quaternion.LookRotation(spawn.right));

            // Switch the backend to this task's scene so agent1/2/3 use the right
            // prompts + voice. Without this the global handler stays on whichever
            // scene was refreshed last at startup (the Training/Hotel race).
            string backendScene = kTaskBackendScenes[taskIndex];
            ServerInterface.RefreshScene(backendScene);

            QoeLog.Event("task", $"teleported to {kTaskLabels[taskIndex]} (backend scene '{backendScene}')");
            SetHud($"Teleported to {kTaskLabels[taskIndex]}");
        }

        // Move the player away from the agent when a run ends, so the stimuli is
        // removed during the rating/break phase. The proximity system then sees
        // no agent in range and clears the active zone. Defaults to world origin
        // when neutralPoint is unassigned.
        void TeleportToNeutral() {
            if (playerTransform == null) {
                QoeLog.Warn("task", "playerTransform not assigned — cannot teleport to neutral");
                return;
            }
            if (neutralPoint != null) {
                playerTransform.SetPositionAndRotation(neutralPoint.position, neutralPoint.rotation);
            } else {
                playerTransform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            }
            QoeLog.Event("task", "teleported to neutral point (stimuli removed)");
        }

        public void AbandonRun() {
            if (phase != DevicePhase.RunningTask && phase != DevicePhase.TaskReceived && phase != DevicePhase.LoadingTask) return;
            QoeLog.Warn("task", $"abandon run {activeRunId} '{activeLabel}' phase={phase} — /end-condition NOT sent");
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            TeleportToNeutral();
            SetHud("Run abandoned locally");
            TransitionPhase(DevicePhase.Idle);
        }

        // ── HTTP ──────────────────────────────────────────────────────────
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

        // ── HUD ───────────────────────────────────────────────────────────
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
            SetBtn(endRunEarlyButton, phase == DevicePhase.RunningTask);
        }

        // Toggle PressDownButton.interactable. The button dims its own backing
        // Image when disabled (and applies the hover tint when enabled), so the
        // disabled state stays visible at a glance without recoloring here.
        static void SetBtn(PressDownButton b, bool enabled) {
            if (b == null) return;
            b.interactable = enabled;
        }

        void SetHud(string s) {
            if (hudText != null) hudText.text = s;
        }
    }
}
