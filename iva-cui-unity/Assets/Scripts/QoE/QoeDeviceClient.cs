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
        Coroutine runCo;
        Coroutine connectTimeoutCo;
        const float ConnectTimeoutS = 10f;

        readonly Queue<string> logLines = new();
        readonly ConcurrentQueue<Action> mainQ = new();

        readonly QoeUI ui = new();
        PressDownButton connectButton, disconnectButton, sendReadyButton, endRunEarlyButton, previewRatingButton;

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
            bool canConnect    = phase == DevicePhase.Idle && !isConnecting && !IsWsOpen;
            bool taskReceived  = phase == DevicePhase.TaskReceived;

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
            topLeftGo = controlsGo = taskGridGo = logPanelGo = connStatusGo = errorGo = null;
            timerText = null; micDot = null; micLabel = null; centerRegion = null;
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

            micLabel = ui.BuildLabel(region, "REC", 13, FontStyles.Bold, kMicRecording, TextAlignmentOptions.Left);
            micLabel.enableWordWrapping = false;
            var mle = micLabel.gameObject.AddComponent<LayoutElement>();
            mle.minWidth = ui.Sx(24); mle.preferredWidth = ui.Sx(24); mle.flexibleWidth = 0;
            mle.minHeight = ui.Sx(18); mle.preferredHeight = ui.Sx(18);
            micLabel.gameObject.SetActive(false);

            timerText = ui.BuildLabel(region, "", 22, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
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
                ui.BuildButton(region, kTaskLabels[i], kTaskBtn, 14, () => TeleportToTask(idx));
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
            TeleportToTask(activeTaskIndex);
            yield return null; // let rig move + camera-follower settle
            QoeLog.Event("ws", $"sending ready for run {activeRunId}");
            SendJson(new { type = WsType.Ready });
            CloseWsIntentional();
            if (ratingClient != null) ratingClient.CloseWs();
            TransitionPhase(DevicePhase.RunningTask);
            runCo = StartCoroutine(RunTaskThenEnd());
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
            if (micLabel != null && micLabel.gameObject.activeSelf != recording)
                micLabel.gameObject.SetActive(recording);
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
            QoeLog.Event("task", $"task finished after {maxDurationS}s — calling /end-condition");
            SetTimer(0);
            TeleportToNeutral();
            yield return PostEndCondition(activeSid);
            runCo = null;
        }

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
            if (phase != DevicePhase.RunningTask && phase != DevicePhase.TaskReceived && phase != DevicePhase.LoadingTask) return;
            QoeLog.Warn("task", $"abandon run {activeRunId} '{activeLabel}' phase={phase} — /end-condition NOT sent");
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
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
