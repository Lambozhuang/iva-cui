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
        // per-phase visibility toggling (see UpdateUiVisibility).
        readonly QoeUI ui = new();
        PressDownButton connectButton, disconnectButton, sendReadyButton, endRunEarlyButton;
        static readonly string[] kTaskLabels = { "Training", "Task 1", "Task 2", "Task 3" };
        // Backend conversation scene to refresh per task (index matches kTaskLabels
        // / taskSpawnPoints). The backend keeps ONE global handler keyed by scene,
        // so we must refresh the right scene when teleporting or agent1/2/3 resolve
        // to the wrong scene's prompt + voice. Tasks 1–3 are all Hotel agents.
        static readonly string[] kTaskBackendScenes = { "Training", "Hotel", "Hotel", "Hotel" };
        TMP_Text hudText;
        TMP_Text logText;
        GameObject hudGo;
        GameObject controlsRowGo;
        GameObject debugRowGo;
        GameObject logPanelGo;

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

        // Debug-only row sitting under the controls row: one teleport button per
        // task (Training / Task 1–3) that jumps the rig straight to that agent
        // and refreshes the backend scene, with no WS / ready / end-condition.
        // Lets the developer hop between agents without the operator console.
        void BuildDebugRow(RectTransform parent) {
            var rowGo = new GameObject("DebugTeleportRow", typeof(RectTransform));
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
            for (int i = 0; i < kTaskLabels.Length; i++) {
                int idx = i;
                var btn = ui.BuildButton(rowRT, kTaskLabels[i], new Color(0.3f, 0.4f, 0.55f), 16,
                    () => TeleportToTask(idx));
                var le = btn.GetComponent<LayoutElement>();
                le.flexibleWidth = 1f;
                le.minHeight = btnH; le.preferredHeight = btnH; le.flexibleHeight = 0;
            }
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
                    SetHud($"Running '{activeLabel}': {remaining}s");
                    lastWhole = remaining;
                }
                yield return null;
            }
            QoeLog.Event("task", $"task finished after {maxDurationS}s — calling /end-condition");
            TeleportToNeutral();
            yield return PostEndCondition(activeSid);
            runCo = null;
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
