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
using UnityEngine.SceneManagement;
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

        [Header("Task scene")]
        // Name of the single task scene this project ships. Loaded additively
        // on top of the shell scene when start_task arrives, unloaded when the
        // run ends. Must be in Build Settings.
        //
        // Two rig topologies are supported:
        //   • Task scene contributes content only — no Camera/AudioListener,
        //     no XR Origin. Leave shellRig empty; the shell scene's rig stays
        //     on for both setup and runtime.
        //   • Task scene brings its own XR Origin, mic handler, locomotion,
        //     etc. (e.g. iva-cui's Hotel_Scene). Assign shellRig below; we
        //     disable it while the task scene is loaded so two rigs don't
        //     fight over head pose / Camera.main / AudioListener, and
        //     re-enable it on unload so the operator UI is visible again
        //     for setup and ratings.
        public string taskSceneName;

        [Tooltip("Optional. Root GameObject of the shell scene's XR Origin (and its AudioListener). Disabled while the task scene is loaded so the task scene's own rig owns Camera.main; re-enabled on unload. Leave null when the task scene contributes content only.")]
        public GameObject shellRig;

        [Header("UI")]
        [Tooltip("Empty RectTransform under a Canvas. HUD label + controls row + log panel + rating section are all built inside it at runtime.")]
        public RectTransform rootContainer;

        [Tooltip("If true, attach LazyCameraFollow to the canvas so it hovers in front of the player camera.")]
        public bool followCamera = true;
        [Tooltip("Distance (m) the canvas sits in front of the camera when followCamera is on.")]
        public float followDistance = 1.5f;
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

        [Header("Controls row")]
        [Tooltip("Width-to-height ratio for each button in the controls row. 2.0 = button is twice as wide as tall.")]
        public float controlsButtonAspect = 2f;

        [Header("Log panel")]
        public int logMaxLines = 12;

        string HttpBase    => $"http://{serverHost}:{serverPort}";
        string WsDeviceUrl => $"ws://{serverHost}:{serverPort}/device";

        // ── Runtime state ────────────────────────────────────────────────
        WebSocket ws;
        bool wsClosedByUs;
        bool isConnecting;
        DevicePhase phase = DevicePhase.Idle;

        string activeSid;
        int activeRunId;
        string activeLabel;
        int maxDurationS;
        Coroutine runCo;
        Coroutine connectTimeoutCo;
        const float ConnectTimeoutS = 10f;

        bool taskSceneLoaded;

        readonly Queue<string> logLines = new();
        readonly ConcurrentQueue<Action> mainQ = new();

        // Code-built UI. References saved here for runtime updates and for
        // per-phase visibility toggling (see UpdateUiVisibility).
        readonly QoeUI ui = new();
        PressDownButton connectButton, disconnectButton, sendReadyButton, endRunEarlyButton;
        PressDownButton debugTaskButton;
        TMP_Text hudText;
        TMP_Text logText;
        GameObject hudGo;
        GameObject controlsRowGo;
        GameObject debugRowGo;
        GameObject logPanelGo;
        // True while a debug task scene is loaded outside the normal session
        // flow (no WS, no ready, no /end-condition). Lets the toggle button
        // know whether to load or unload, and lets UpdateUiVisibility hide the
        // controls/HUD just like during a real RunningTask.
        bool debugTaskActive;

        // ── Lifecycle ────────────────────────────────────────────────────
        void OnEnable()  { Application.logMessageReceived += OnUnityLog; }
        void OnDisable() { Application.logMessageReceived -= OnUnityLog; }

        void Start() {
            QoeLog.Event("init", $"server={HttpBase} kind={deviceKind} taskScene={(string.IsNullOrEmpty(taskSceneName) ? "NOT SET" : taskSceneName)}");
            ValidateTaskSceneInBuildSettings();
            BuildUi();
            SetHud("Ready — press Connect");
            UpdateButtonStates();
        }

        // ── UI ───────────────────────────────────────────────────────────
        // Builds the entire merged HUD inside rootContainer (a single canvas
        // hosts both device controls and the rating section):
        //   [HUD label]
        //   [Connect | Disconnect | Ready | End Run] (equal-width row)
        //   [Log panel]
        //   [Rating section — status bar + scrolling form area, flex height]
        // rootContainer's vertical layout group stacks them in that order.
        void BuildUi() {
            if (rootContainer == null) {
                QoeLog.Err("ui", "rootContainer not assigned — cannot build device UI");
                return;
            }
            for (int i = rootContainer.childCount - 1; i >= 0; i--) {
                var child = rootContainer.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
            Canvas.ForceUpdateCanvases();
            float w = rootContainer.rect.width;
            ui.scale = w > 0 ? Mathf.Clamp(w / 600f, 0.05f, 4f) : 1f;

            var rootVlg = rootContainer.GetComponent<VerticalLayoutGroup>();
            if (rootVlg == null) rootVlg = rootContainer.gameObject.AddComponent<VerticalLayoutGroup>();
            rootVlg.spacing = ui.Sx(6);
            rootVlg.padding = new RectOffset(ui.Sx(8), ui.Sx(8), ui.Sx(8), ui.Sx(8));
            rootVlg.childForceExpandWidth = true; rootVlg.childForceExpandHeight = false;
            rootVlg.childControlWidth = true; rootVlg.childControlHeight = true;

            hudText = ui.BuildLabel(rootContainer, "", 18, FontStyles.Bold, new Color(0.1f, 0.3f, 0.7f));
            hudGo = hudText.gameObject;
            var hudLe = hudGo.AddComponent<LayoutElement>();
            hudLe.minHeight = ui.Sx(28); hudLe.preferredHeight = ui.Sx(28);

            BuildControlsRow(rootContainer);
            if (debugMode) BuildDebugRow(rootContainer);
            if (debugMode) BuildLogPanel(rootContainer);

            if (ratingClient != null) {
                ratingClient.BuildUi(rootContainer, debugMode);
                ratingClient.OnFormVisibilityChanged = _ => UpdateUiVisibility();
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(rootContainer);
            AttachCanvasFollower();
            UpdateUiVisibility();
        }

        // Visibility rules:
        //   debugMode on  → always show everything (HUD label + controls + log + rating section).
        //   debugMode off → hide HUD label + controls during RunningTask so the
        //                   subject sees only the task scene; restore on next phase.
        //                   When a rating form is visible, hide controls + HUD
        //                   so the subject only sees the rating UI.
        void UpdateUiVisibility() {
            if (rootContainer == null) return;
            bool ratingVisible = ratingClient != null && ratingClient.IsFormVisible;
            bool running = phase == DevicePhase.RunningTask;
            bool showCanvas = debugMode || !running;
            bool showControls = debugMode || (!running && !ratingVisible);
            bool showHud = debugMode || (!running && !ratingVisible);
            rootContainer.gameObject.SetActive(showCanvas);
            if (hudGo != null) hudGo.SetActive(showHud);
            if (controlsRowGo != null) controlsRowGo.SetActive(showControls);
            if (debugRowGo != null) debugRowGo.SetActive(debugMode);
            if (logPanelGo != null) logPanelGo.SetActive(debugMode);
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
            hudGo = controlsRowGo = debugRowGo = logPanelGo = null;
        }

        void BuildControlsRow(RectTransform parent) {
            var rowGo = new GameObject("ControlsRow", typeof(RectTransform));
            controlsRowGo = rowGo;
            rowGo.transform.SetParent(parent, false);
            var hg = rowGo.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = ui.Sx(8);
            // childForceExpandHeight=false: the row honors each button's
            // preferredHeight instead of stretching them to fill row height.
            // childForceExpandWidth=true + flexibleWidth=1 below: buttons split
            // remaining width equally.
            hg.childForceExpandWidth = true; hg.childForceExpandHeight = false;
            hg.childControlWidth = true; hg.childControlHeight = true;
            hg.childAlignment = TextAnchor.MiddleCenter;

            // Each button is equal-width: btnW = (rowInner − 3·spacing) / 4.
            // Height follows from controlsButtonAspect (w/h), so widening the
            // canvas grows buttons proportionally and the row's height stays
            // tied to button width — independent of how tall the canvas is.
            float aspect = Mathf.Max(0.1f, controlsButtonAspect);
            float rowInner = Mathf.Max(1f, parent.rect.width - 2f * ui.Sx(8));
            float btnW = (rowInner - 3f * ui.Sx(8)) / 4f;
            int btnH = Mathf.Max(ui.Sx(20), Mathf.RoundToInt(btnW / aspect));
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.minHeight = btnH; rowLe.preferredHeight = btnH; rowLe.flexibleHeight = 0;

            var rowRT = (RectTransform)rowGo.transform;
            connectButton     = ui.BuildButton(rowRT, "Connect",    new Color(0.16f, 0.5f, 0.95f),  22, ConnectManual);
            disconnectButton  = ui.BuildButton(rowRT, "Disconnect", new Color(0.55f, 0.55f, 0.6f),  22, DisconnectManual);
            sendReadyButton   = ui.BuildButton(rowRT, "Ready",      new Color(0.2f, 0.7f, 0.35f),   22, SendReadyManual);
            endRunEarlyButton = ui.BuildButton(rowRT, "End Run",    new Color(0.8f, 0.35f, 0.25f),  22, EndRunEarly);
            foreach (var b in new[] { connectButton, disconnectButton, sendReadyButton, endRunEarlyButton }) {
                var le = b.GetComponent<LayoutElement>();
                le.flexibleWidth = 1f;
                le.minHeight = btnH; le.preferredHeight = btnH; le.flexibleHeight = 0;
            }
        }

        // Debug-only row sitting under the controls row. Holds the Debug task
        // toggle (and could grow more buttons later). Sized to match a single
        // controls-row cell so it doesn't dominate vertically.
        void BuildDebugRow(RectTransform parent) {
            var rowGo = new GameObject("DebugRow", typeof(RectTransform));
            debugRowGo = rowGo;
            rowGo.transform.SetParent(parent, false);
            var hg = rowGo.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = ui.Sx(8);
            hg.childForceExpandWidth = true; hg.childForceExpandHeight = false;
            hg.childControlWidth = true; hg.childControlHeight = true;
            hg.childAlignment = TextAnchor.MiddleCenter;

            // Match the controls row's per-button height for visual continuity.
            float aspect = Mathf.Max(0.1f, controlsButtonAspect);
            float rowInner = Mathf.Max(1f, parent.rect.width - 2f * ui.Sx(8));
            float btnW = (rowInner - 3f * ui.Sx(8)) / 4f;
            int btnH = Mathf.Max(ui.Sx(20), Mathf.RoundToInt(btnW / aspect));
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.minHeight = btnH; rowLe.preferredHeight = btnH; rowLe.flexibleHeight = 0;

            var rowRT = (RectTransform)rowGo.transform;
            debugTaskButton = ui.BuildButton(rowRT, debugTaskActive ? "Stop task" : "Debug task",
                new Color(0.45f, 0.45f, 0.5f), 18, ToggleDebugTask);
            var le = debugTaskButton.GetComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minHeight = btnH; le.preferredHeight = btnH; le.flexibleHeight = 0;
        }

        void BuildLogPanel(RectTransform parent) {
            var panelGo = new GameObject("Log", typeof(RectTransform), typeof(Image));
            logPanelGo = panelGo;
            panelGo.transform.SetParent(parent, false);
            panelGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
            var panelLe = panelGo.AddComponent<LayoutElement>();
            panelLe.minHeight = ui.Sx(140); panelLe.preferredHeight = ui.Sx(140);

            logText = ui.BuildLabel((RectTransform)panelGo.transform, "", 12, FontStyles.Normal, new Color(0.9f, 0.9f, 0.9f));
            logText.enableWordWrapping = false;
            QoeUI.StretchToParent((RectTransform)logText.transform);
            // Inset a bit so the text doesn't kiss the panel edges.
            var rt = (RectTransform)logText.transform;
            rt.offsetMin = new Vector2(ui.Sx(6), ui.Sx(6));
            rt.offsetMax = new Vector2(-ui.Sx(6), -ui.Sx(6));
        }

        void ValidateTaskSceneInBuildSettings() {
            if (string.IsNullOrEmpty(taskSceneName)) return;
            int count = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < count; i++) {
                var path = SceneUtility.GetScenePathByBuildIndex(i);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == taskSceneName) return;
            }
            QoeLog.Warn("task", $"taskSceneName '{taskSceneName}' not in Build Settings — load will fail");
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
            UnloadTaskScene();
            CloseWsIntentional();
            TransitionPhase(DevicePhase.Idle);
            SetHud("Disconnected — press Connect");
        }

        public void SendReadyManual() {
            if (phase != DevicePhase.TaskReceived) return;
            TransitionPhase(DevicePhase.LoadingTask);
            SetHud($"Loading task '{activeLabel}'…");
            StartCoroutine(LoadThenStart());
        }

        IEnumerator LoadThenStart() {
            yield return StartCoroutine(LoadTaskScene());
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
        }

        async void OnDestroy() {
            _ = UnloadTaskScene();
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

            QoeLog.Event("task", $"start_task: label='{activeLabel}' duration={maxDurationS}s run={activeRunId}");
            if (string.IsNullOrEmpty(activeSid)) QoeLog.Warn("task", "session_id is null/empty in start_task payload");

            TransitionPhase(DevicePhase.TaskReceived);
            SetHud($"Task received ('{activeLabel}') — press Ready to load and start");
        }

        IEnumerator RunTaskThenEnd() {
            QoeLog.Event("task", $"task started — label='{activeLabel}' maxDuration={maxDurationS}s");
            float t = 0; int lastWhole = -1;
            while (t < maxDurationS) {
                t += Time.deltaTime;
                int remaining = Mathf.CeilToInt(maxDurationS - t);
                if (remaining != lastWhole) {
                    SetHud($"Running '{activeLabel}': {remaining}s");
                    lastWhole = remaining;
                }
                yield return null;
            }
            QoeLog.Event("task", $"task finished after {maxDurationS}s — calling /end-condition");
            UnloadTaskScene();
            yield return PostEndCondition(activeSid);
            runCo = null;
        }

        public void EndRunEarly() {
            if (phase != DevicePhase.RunningTask) return;
            QoeLog.Event("task", $"end run early: run {activeRunId} '{activeLabel}'");
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            UnloadTaskScene();
            SetHud("Ending run early — calling /end-condition…");
            StartCoroutine(PostEndCondition(activeSid));
        }

        // Debug-only: load/unload the task scene without any networking
        // (no WS, no /ready, no /end-condition, no rating). Lets the developer
        // iterate on the in-scene XR setup, lighting, scripts, etc. without
        // standing up the Express server. Refuses to engage during a real
        // run so a misclick can't cross the wires.
        public void ToggleDebugTask() {
            if (phase == DevicePhase.RunningTask || phase == DevicePhase.TaskReceived || phase == DevicePhase.LoadingTask) {
                QoeLog.Warn("task", $"debug task toggle ignored — already in {phase}");
                return;
            }
            if (debugTaskActive) StartCoroutine(DebugTaskUnload());
            else                 StartCoroutine(DebugTaskLoad());
        }

        IEnumerator DebugTaskLoad() {
            QoeLog.Event("task", $"debug load: '{taskSceneName}'");
            SetHud($"[debug] Loading '{taskSceneName}'…");
            debugTaskActive = true;
            UpdateDebugTaskButtonLabel();
            UpdateUiVisibility();
            yield return StartCoroutine(LoadTaskScene());
            SetHud("[debug] task scene loaded — press Stop task to unload");
        }

        IEnumerator DebugTaskUnload() {
            QoeLog.Event("task", "debug unload");
            SetHud("[debug] unloading task scene…");
            var op = UnloadTaskScene();
            if (op != null) yield return op;
            debugTaskActive = false;
            UpdateDebugTaskButtonLabel();
            UpdateUiVisibility();
            SetHud("[debug] task scene unloaded");
        }

        void UpdateDebugTaskButtonLabel() {
            if (debugTaskButton == null) return;
            var lbl = debugTaskButton.GetComponentInChildren<TMP_Text>();
            if (lbl != null) lbl.text = debugTaskActive ? "Stop task" : "Debug task";
        }

        public void AbandonRun() {
            if (phase != DevicePhase.RunningTask && phase != DevicePhase.TaskReceived && phase != DevicePhase.LoadingTask) return;
            QoeLog.Warn("task", $"abandon run {activeRunId} '{activeLabel}' phase={phase} — /end-condition NOT sent");
            if (runCo != null) { StopCoroutine(runCo); runCo = null; }
            UnloadTaskScene();
            SetHud("Run abandoned locally");
            TransitionPhase(DevicePhase.Idle);
        }

        // ── Task scene load/unload ────────────────────────────────────────
        // Sequenced load to avoid a rig-binding race: Hotel_Scene's Awake/Start
        // (XROrigin discovery in ApplyVRSettings, TrackedPoseDriver wiring, mic
        // handler, etc.) must run with only ONE rig active in the world. If we
        // disable the shell rig after activation, both rigs were briefly enabled
        // when Hotel's scripts ran — Hotel's TrackedPoseDriver then fails to
        // claim the HMD pose and the camera renders at floor height with no
        // tracking. So:
        //   1. Start the additive load with allowSceneActivation = false.
        //   2. Wait until Unity reports load progress 0.9 (loaded but parked).
        //   3. Disable the shell rig — this leaves the world with zero active
        //      rigs for one frame, but no Hotel script has run yet.
        //   4. Flip allowSceneActivation = true. Hotel's Awake/Start now runs
        //      against a clean world and binds correctly to its own rig.
        IEnumerator LoadTaskScene() {
            if (string.IsNullOrEmpty(taskSceneName)) {
                QoeLog.Warn("task", "taskSceneName is empty — skipping load");
                yield break;
            }
            if (taskSceneLoaded) {
                QoeLog.Warn("task", $"scene '{taskSceneName}' already loaded — unloading first");
                yield return SceneManager.UnloadSceneAsync(taskSceneName);
                taskSceneLoaded = false;
            }
            var op = SceneManager.LoadSceneAsync(taskSceneName, LoadSceneMode.Additive);
            op.allowSceneActivation = false;
            // Unity stalls progress at 0.9 while waiting for activation.
            while (op.progress < 0.9f) yield return null;

            if (shellRig != null) shellRig.SetActive(false);
            // One frame for the disable to settle (XR plugin re-evaluates the
            // active TrackedPoseDriver / camera at end-of-frame).
            yield return null;

            op.allowSceneActivation = true;
            while (!op.isDone) yield return null;

            taskSceneLoaded = true;
            var loaded = SceneManager.GetSceneByName(taskSceneName);
            if (loaded.IsValid()) SceneManager.SetActiveScene(loaded);
            RetargetCanvasFollowerToActiveCamera();
        }

        AsyncOperation UnloadTaskScene() {
            if (!taskSceneLoaded) return null;
            taskSceneLoaded = false;
            var op = SceneManager.UnloadSceneAsync(taskSceneName);
            if (op != null) {
                op.completed += _ => {
                    var active = SceneManager.GetActiveScene();
                    if (!active.IsValid() || active.name == taskSceneName) {
                        for (int i = 0; i < SceneManager.sceneCount; i++) {
                            var s = SceneManager.GetSceneAt(i);
                            if (s.IsValid() && s.name != taskSceneName) { SceneManager.SetActiveScene(s); break; }
                        }
                    }
                    // Hand the rig back: re-enable shell rig before doing
                    // anything that needs Camera.main (the rating WS open
                    // below, the canvas follower retarget). Order matters —
                    // the task rig was just destroyed with its scene, so
                    // Camera.main is null until the shell rig wakes up.
                    if (shellRig != null) shellRig.SetActive(true);
                    RetargetCanvasFollowerToActiveCamera();
                    // Open the rating WS now (after unload, not during
                    // active_condition) so request_rating fires while the
                    // shell rig owns the camera. The operator console
                    // re-issues request_rating on rating-WS reconnect, so
                    // any owed rating for this run lands automatically.
                    // Skip when the operator is disconnecting (Idle phase
                    // with the device WS closed by us): the user wants the
                    // device fully off, not the rating side reconnected.
                    bool operatorDisconnect = phase == DevicePhase.Idle && wsClosedByUs;
                    if (ratingClient != null && !operatorDisconnect) ratingClient.OpenWs(serverHost, serverPort);
                };
            }
            return op;
        }

        // Refresh LazyCameraFollow.cam after a rig swap. The shell rig and
        // task rig own different Camera.main transforms; without this the
        // canvas would either keep tracking a destroyed transform or sit
        // at world-origin until the user looks past the dead-zone.
        void RetargetCanvasFollowerToActiveCamera() {
            if (rootContainer == null) return;
            var canvas = rootContainer.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            var follow = root.GetComponent<LazyCameraFollow>();
            if (follow == null) return;
            // Force a re-resolve: clear the stale transform so the helper
            // re-picks up Camera.main. followCameraTarget overrides if set.
            follow.cam = followCameraTarget;
            follow.ResolveMainCameraIfMissing();
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

        // Toggle PressDownButton.interactable and dim the backing Image so the
        // disabled state is visible at a glance.
        static void SetBtn(PressDownButton b, bool enabled) {
            if (b == null) return;
            b.interactable = enabled;
            var img = b.GetComponent<Image>();
            if (img != null) {
                var c = img.color;
                img.color = new Color(c.r, c.g, c.b, enabled ? 1f : 0.35f);
            }
        }

        void SetHud(string s) {
            if (hudText != null) hudText.text = s;
        }
    }
}
