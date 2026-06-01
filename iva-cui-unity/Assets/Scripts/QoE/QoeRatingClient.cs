using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NativeWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace QoeDevice {
    /// <summary>
    /// On-headset rating client. Mirrors rating-client/src/RatingClientPage.tsx:
    ///  • opens a WS to /rating-client and sends `hello`
    ///  • on `request_rating`, builds a UGUI form under <see cref="formContainer"/>
    ///    matching the four item kinds (radio_scale, slider_scale, matrix, group)
    ///  • POSTs /ratings on submit, then clears the form
    ///
    /// Lifecycle and UI parenting are driven by QoeDeviceClient: it calls
    /// <see cref="BuildUi"/> with a section RectTransform inside its own canvas
    /// (Phase B — single merged canvas), <see cref="OpenWs"/> after end-condition
    /// reconnect, and <see cref="CloseWs"/> when the subject hits Ready.
    ///
    /// All sizes (fonts, fixed-label widths, row heights, slider thickness) are
    /// scaled at build time by parent.rect.width / kReferenceWidth, so a
    /// narrower canvas shrinks the form rather than overflowing.
    /// </summary>
    public class QoeRatingClient : MonoBehaviour {
        // Layout sizes are authored against a 600-wide form. Anything narrower
        // shrinks proportionally; anything wider scales up.
        const float kReferenceWidth = 600f;

        [Header("Identity (sent in hello)")]
        public string deviceKind = "rating-client";
        public string deviceName = "Quest 3 Rating";

        // ── Runtime ──────────────────────────────────────────────────────
        WebSocket ws;
        bool wsClosedByUs;
        readonly ConcurrentQueue<Action> mainQ = new();

        string serverHost;
        int serverPort;
        string HttpBase => $"http://{serverHost}:{serverPort}";
        string WsUrl   => $"ws://{serverHost}:{serverPort}/rating-client";

        string activeSid;
        int activeRunId;
        string activeLabel;
        bool debugMode;
        readonly Dictionary<string, object> answers = new();
        readonly List<Func<bool>> leafChecks = new();
        Button submitButton;
        readonly QoeUI ui = new();

        // Built at runtime by BuildUi(parent). Section is the rating client's
        // own RectTransform inside the merged canvas — clearing/rebuilding the
        // form swaps children of formContainer without touching device-client UI.
        RectTransform section;
        RectTransform formContainer;
        TMP_Text statusText;
        GameObject statusBarGo;
        bool sectionDebugMode;

        public bool IsOpen => ws != null && ws.State == WebSocketState.Open;

        // True while a rating form is rendered inside formContainer (i.e. a
        // request_rating arrived or a debug preview was loaded). Flipped to
        // false on ClearForm / submit. QoeDeviceClient subscribes to
        // OnFormVisibilityChanged to hide its controls while the subject is
        // answering, so they only see the rating UI.
        public bool IsFormVisible { get; private set; }
        public Action<bool> OnFormVisibilityChanged;

        // QoeDeviceClient drives BuildUi as part of its own UI build, so we
        // intentionally do nothing in Start. This keeps the rating client a
        // passive child of the merged canvas.

        // Builds a vertical "rating section" under <paramref name="parent"/>:
        // a status bar on top and a scrolling form area below. Caller (device
        // client) wraps this section inside its own VerticalLayoutGroup, so
        // the section's LayoutElement.flexibleHeight=1 makes the form area
        // soak up whatever space is left after HUD/controls/log.
        // <paramref name="showDebugButton"/> mirrors QoeDeviceClient.debugMode
        // — off in subject-facing builds, on while iterating.
        public void BuildUi(RectTransform parent, bool showDebugButton = true) {
            if (parent == null) {
                QoeLog.Err("rating", "BuildUi called with null parent — cannot build rating UI");
                return;
            }
            // If a previous build exists, nuke it so re-runs don't pile up.
            // Reset IsFormVisible too — a stale 'true' from a prior preview
            // would otherwise trick QoeDeviceClient into hiding controls
            // because it thinks a form is still up.
            if (section != null) {
                if (Application.isPlaying) Destroy(section.gameObject); else DestroyImmediate(section.gameObject);
                section = null; formContainer = null; statusText = null; statusBarGo = null;
            }
            IsFormVisible = false;
            sectionDebugMode = showDebugButton;
            Canvas.ForceUpdateCanvases();
            float w = parent.rect.width;
            ui.scale = w > 0 ? Mathf.Clamp(w / kReferenceWidth, 0.05f, 4f) : 1f;

            var sectionGo = new GameObject("RatingSection", typeof(RectTransform));
            sectionGo.transform.SetParent(parent, false);
            section = (RectTransform)sectionGo.transform;
            var vlg = sectionGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = ui.Sx(6);
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            var sectionLe = sectionGo.AddComponent<LayoutElement>();
            sectionLe.flexibleHeight = 1f; sectionLe.minHeight = ui.Sx(200);

            BuildStatusBar(section, showDebugButton);
            BuildFormArea(section);

            LayoutRebuilder.ForceRebuildLayoutImmediate(section);
            ApplySectionVisibility();
        }

        // When debug is off, hide the entire rating section (status bar +
        // form area) until a form arrives. Status text exposes the active
        // condition label, so subjects must never see it. With debug on the
        // section is always visible. With debug off it appears only while a
        // form is being answered, and even then the status bar is hidden.
        void ApplySectionVisibility() {
            bool sectionVisible = sectionDebugMode || IsFormVisible;
            if (section != null) section.gameObject.SetActive(sectionVisible);
            if (statusBarGo != null) statusBarGo.SetActive(sectionDebugMode);
        }

        void BuildStatusBar(RectTransform parent, bool showDebugButton) {
            var rowGo = new GameObject("StatusBar", typeof(RectTransform));
            statusBarGo = rowGo;
            rowGo.transform.SetParent(parent, false);
            var hg = rowGo.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = ui.Sx(6);
            hg.childForceExpandWidth = false; hg.childForceExpandHeight = false;
            hg.childControlWidth = true; hg.childControlHeight = true;
            hg.childAlignment = TextAnchor.MiddleLeft;
            // Single-line status text + debug button. Use the button's
            // preferred height so the row stays as compact as the button.
            int rowH = ui.Sx(24);
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.minHeight = rowH; rowLe.preferredHeight = rowH; rowLe.flexibleHeight = 0;

            statusText = ui.BuildLabel((RectTransform)rowGo.transform, "Disconnected", 12, FontStyles.Normal, new Color(0.4f, 0.4f, 0.4f));
            statusText.enableWordWrapping = false;
            statusText.overflowMode = TextOverflowModes.Ellipsis;
            var statusLe = statusText.gameObject.AddComponent<LayoutElement>();
            statusLe.flexibleWidth = 1f; statusLe.minWidth = 0; statusLe.preferredWidth = 0;
            statusLe.minHeight = rowH; statusLe.preferredHeight = rowH;

            if (showDebugButton) {
                var debugBtn = ui.BuildButton((RectTransform)rowGo.transform, "Debug preview", new Color(0.45f, 0.45f, 0.5f), 12, LoadDebugPreview);
                var debugLe = debugBtn.GetComponent<LayoutElement>();
                debugLe.preferredWidth = ui.Sx(110); debugLe.minWidth = ui.Sx(110); debugLe.flexibleWidth = 0;
                debugLe.minHeight = rowH; debugLe.preferredHeight = rowH; debugLe.flexibleHeight = 0;
            }
        }

        // The form area takes whatever vertical space rootContainer has left.
        // BuildForm builds its ScrollRect inside this rect at request time.
        void BuildFormArea(RectTransform parent) {
            var go = new GameObject("FormArea", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            formContainer = (RectTransform)go.transform;
            var le = go.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f; le.minHeight = ui.Sx(200);
        }

        void Update() {
            // Upstream NativeWebSocket buffers callbacks until DispatchMessageQueue
            // is pumped on the main thread; without this, OnOpen/OnMessage/OnClose
            // never fire on non-WebGL platforms.
#if !UNITY_WEBGL || UNITY_EDITOR
            ws?.DispatchMessageQueue();
#endif
            while (mainQ.TryDequeue(out var a)) a();
        }

        async void OnDestroy() {
            if (ws != null) { wsClosedByUs = true; await ws.Close(); }
        }

        // ── Public API ───────────────────────────────────────────────────
        public async void OpenWs(string host, int port) {
            if (ws != null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.Connecting)) return;
            serverHost = host;
            serverPort = port;
            wsClosedByUs = false;
            QoeLog.Event("rating", $"connecting to {WsUrl}");
            SetStatus("Connecting…");
            ws = new WebSocket(WsUrl);

            ws.OnOpen += () => mainQ.Enqueue(() => {
                SetStatus("Waiting for rating request…");
                SendJson(new { type = "hello", data = new { kind = deviceKind, name = deviceName } });
            });

            ws.OnMessage += (bytes) => {
                // Upstream NativeWebSocket (endel) hands back the full payload
                // buffer — no offset/length pair like the Meta XR SDK fork.
                if (bytes == null || bytes.Length == 0) return;
                var raw = Encoding.UTF8.GetString(bytes);
                mainQ.Enqueue(() => HandleWsMessage(raw));
            };

            ws.OnError += err => mainQ.Enqueue(() => {
                QoeLog.Err("rating", err);
                SetStatus($"WS error: {err}");
            });

            ws.OnClose += code => mainQ.Enqueue(() => {
                if (!wsClosedByUs) QoeLog.Warn("rating", $"unexpected close (code {code})");
                SetStatus(wsClosedByUs ? "Disconnected" : $"Disconnected (code {code})");
                ws = null;
            });

            await ws.Connect();
        }

        public async void CloseWs() {
            if (ws == null) { ClearForm(); return; }
            wsClosedByUs = true;
            ClearForm();
            await ws.Close();
        }

        public void LoadDebugPreview() {
            if (formContainer == null) {
                QoeLog.Warn("rating", "LoadDebugPreview before BuildUi — run Device: build UI first");
                return;
            }
            activeSid = "";
            activeRunId = 0;
            activeLabel = "Debug preview";
            debugMode = true;
            BuildForm(JArray.Parse(DEBUG_PREVIEW_JSON));
        }

        // ── WS handling ──────────────────────────────────────────────────
        async void SendJson(object obj) {
            if (ws == null || ws.State != WebSocketState.Open) return;
            var json = JsonConvert.SerializeObject(obj);
            await ws.SendText(json);
        }

        void HandleWsMessage(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return;
            JObject env;
            try { env = JObject.Parse(raw); }
            catch (Exception e) { QoeLog.Err("rating", $"parse: {e.Message}  raw={raw}"); return; }

            var type = env["type"]?.ToString();
            if (type != "request_rating") return;

            var d = env["data"] as JObject;
            if (d == null) { QoeLog.Warn("rating", "request_rating with no data"); return; }
            activeSid   = d["session_id"]?.ToString();
            activeRunId = d["condition_run_id"]?.ToObject<int>() ?? 0;
            activeLabel = d["label"]?.ToString() ?? "";
            debugMode = false;
            var items = d["rating_config"]?["items"] as JArray;
            if (items == null || items.Count == 0) { QoeLog.Warn("rating", "request_rating with no items"); return; }
            BuildForm(items);
        }

        // ── Form lifecycle ───────────────────────────────────────────────
        void ClearForm() {
            answers.Clear();
            leafChecks.Clear();
            submitButton = null;
            if (formContainer == null) return;
            for (int i = formContainer.childCount - 1; i >= 0; i--) {
                var child = formContainer.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
            SetFormVisible(false);
        }

        void SetFormVisible(bool v) {
            if (IsFormVisible == v) return;
            IsFormVisible = v;
            ApplySectionVisibility();
            OnFormVisibilityChanged?.Invoke(v);
        }

        void BuildForm(JArray items) {
            if (formContainer == null) {
                QoeLog.Err("rating", "formContainer not built — call BuildUi first");
                return;
            }
            ClearForm();
            Canvas.ForceUpdateCanvases();
            float w = formContainer.rect.width;
            ui.scale = w > 0 ? Mathf.Clamp(w / kReferenceWidth, 0.05f, 4f) : 1f;

            // Build a ScrollRect inside formContainer so the form fits the rect
            // the user sized in the inspector — long forms scroll vertically
            // instead of overflowing several meters into the scene.
            var content = ui.BuildScrollRect(formContainer);
            SetStatus($"Rate condition: {activeLabel}");

            foreach (var item in items) {
                RenderItem(item, new List<string>(), content);
            }
            BuildSubmitButton(content);
            UpdateSubmitInteractable();

            // Force a layout pass now so the form is visible immediately —
            // important for edit-mode previews where there's no Update tick.
            LayoutRebuilder.ForceRebuildLayoutImmediate(formContainer);
            Canvas.ForceUpdateCanvases();
            SetFormVisible(true);
        }

        void RenderItem(JToken item, List<string> path, RectTransform parent) {
            var render = item["render"]?.ToString();
            var id = item["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) return;
            var leafPath = new List<string>(path) { id };

            switch (render) {
                case "radio_scale":  BuildRadioScale(item, leafPath, parent);  break;
                case "slider_scale": BuildSliderScale(item, leafPath, parent); break;
                case "matrix":       BuildMatrix(item, leafPath, parent);       break;
                case "group":        BuildGroup(item, leafPath, parent);        break;
                default: QoeLog.Warn("rating", $"unknown render kind: {render}"); break;
            }
        }

        void BuildGroup(JToken item, List<string> path, RectTransform parent) {
            var panel = ui.BuildPanel(parent, "Group");
            var title = item["title"]?.ToString();
            var instr = item["instruction"]?.ToString();
            if (!string.IsNullOrEmpty(title)) ui.BuildLabel(panel, title, 20, FontStyles.Bold);
            if (!string.IsNullOrEmpty(instr)) ui.BuildLabel(panel, instr, 14, FontStyles.Normal, new Color(0.4f, 0.4f, 0.4f));
            var subItems = item["items"] as JArray;
            if (subItems == null) return;
            foreach (var sub in subItems) RenderItem(sub, path, panel);
        }

        void BuildRadioScale(JToken item, List<string> path, RectTransform parent) {
            var question = item["question"]?.ToString() ?? "";
            int min = item["scale_min"]?.ToObject<int>() ?? 1;
            int max = item["scale_max"]?.ToObject<int>() ?? 5;
            var labels = item["point_labels"] as JArray;

            var panel = ui.BuildPanel(parent, "RadioScale");
            ui.BuildLabel(panel, question, 16, FontStyles.Bold);

            var rowGo = new GameObject("Options", typeof(RectTransform));
            rowGo.transform.SetParent(panel, false);
            var hg = rowGo.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = ui.Sx(4); hg.childForceExpandWidth = true; hg.childForceExpandHeight = true;
            hg.childControlWidth = true; hg.childControlHeight = true;
            var rowLe = rowGo.AddComponent<LayoutElement>();
            rowLe.minHeight = ui.Sx(56);

            int? selected = null;
            var imgs = new List<Image>();
            for (int i = 0; i < max - min + 1; i++) {
                int v = min + i;
                var labelText = (labels != null && i < labels.Count) ? labels[i].ToString() : "";

                var btnGo = new GameObject($"Btn_{v}", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(PressDownButton));
                btnGo.transform.SetParent(rowGo.transform, false);
                var img = btnGo.GetComponent<Image>();
                img.color = Color.white;
                imgs.Add(img);
                btnGo.GetComponent<Outline>().effectColor = new Color(0.7f, 0.7f, 0.7f);
                var btn = btnGo.GetComponent<PressDownButton>();

                var inner = new GameObject("Inner", typeof(RectTransform));
                inner.transform.SetParent(btnGo.transform, false);
                var innerVlg = inner.AddComponent<VerticalLayoutGroup>();
                innerVlg.childAlignment = TextAnchor.MiddleCenter;
                innerVlg.childForceExpandWidth = true;
                innerVlg.childForceExpandHeight = false;
                innerVlg.childControlWidth = true;
                innerVlg.childControlHeight = true;
                QoeUI.StretchToParent((RectTransform)inner.transform);
                ui.BuildLabel((RectTransform)inner.transform, v.ToString(), 18, FontStyles.Bold, null, TextAlignmentOptions.Center);
                ui.BuildLabel((RectTransform)inner.transform, labelText, 12, FontStyles.Normal, new Color(0.4f, 0.4f, 0.4f), TextAlignmentOptions.Center);

                int captured = v;
                int idx = i;
                btn.onPress = () => {
                    selected = captured;
                    SetAt(path, new Dictionary<string, object> { ["score"] = captured });
                    for (int j = 0; j < imgs.Count; j++) {
                        imgs[j].color = (j == idx) ? new Color(0.16f, 0.5f, 0.95f) : Color.white;
                    }
                    UpdateSubmitInteractable();
                };
            }
            leafChecks.Add(() => selected != null);
        }

        void BuildSliderScale(JToken item, List<string> path, RectTransform parent) {
            var question = item["question"]?.ToString() ?? "";
            int min = item["scale_min"]?.ToObject<int>() ?? 0;
            int max = item["scale_max"]?.ToObject<int>() ?? 100;
            var minLabel = item["min_label"]?.ToString();
            var maxLabel = item["max_label"]?.ToString();

            var panel = ui.BuildPanel(parent, "SliderScale");
            ui.BuildLabel(panel, question, 16, FontStyles.Bold);

            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(panel, false);
            var hg = row.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = ui.Sx(6); hg.childAlignment = TextAnchor.MiddleLeft;
            hg.childForceExpandWidth = false; hg.childForceExpandHeight = false;
            hg.childControlWidth = true; hg.childControlHeight = true;
            var rowLe = row.AddComponent<LayoutElement>();
            rowLe.minHeight = ui.Sx(36);

            // Min/max labels and value readout get fixed pixel widths so the
            // slider track gets all remaining space. The readout has a fixed
            // width too — without it, "0"→"100" makes the track shrink as the
            // user slides, which feels broken.
            if (!string.IsNullOrEmpty(minLabel)) ui.BuildPxLabel((RectTransform)row.transform, minLabel, ui.Sx(70), TextAlignmentOptions.Right, 12);

            // Slider track (background) — flex 1 so it consumes leftover space.
            var sliderGo = new GameObject("Slider", typeof(RectTransform), typeof(Image), typeof(Slider));
            sliderGo.transform.SetParent(row.transform, false);
            sliderGo.GetComponent<Image>().color = new Color(0.85f, 0.85f, 0.85f);
            var sliderLe = sliderGo.AddComponent<LayoutElement>();
            sliderLe.flexibleWidth = 1f; sliderLe.minWidth = 0; sliderLe.preferredWidth = 0;
            sliderLe.minHeight = ui.Sx(18); sliderLe.preferredHeight = ui.Sx(18);
            var slider = sliderGo.GetComponent<Slider>();

            // Fill (blue progress) — fillArea spans the full track. Any inset
            // here makes the fill start at +offsetMin, so even at value 0 a
            // gray strip shows at the left edge.
            var fillArea = new GameObject("FillArea", typeof(RectTransform));
            fillArea.transform.SetParent(sliderGo.transform, false);
            var fillAreaRT = (RectTransform)fillArea.transform;
            fillAreaRT.anchorMin = new Vector2(0, 0); fillAreaRT.anchorMax = new Vector2(1, 1);
            fillAreaRT.offsetMin = Vector2.zero; fillAreaRT.offsetMax = Vector2.zero;
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            var fillRT = (RectTransform)fill.transform;
            fillRT.anchorMin = Vector2.zero; fillRT.anchorMax = Vector2.one; fillRT.sizeDelta = Vector2.zero;
            fill.GetComponent<Image>().color = new Color(0.3f, 0.55f, 0.95f);
            slider.fillRect = fillRT;

            // Handle
            var handleArea = new GameObject("HandleArea", typeof(RectTransform));
            handleArea.transform.SetParent(sliderGo.transform, false);
            var handleAreaRT = (RectTransform)handleArea.transform;
            handleAreaRT.anchorMin = new Vector2(0, 0); handleAreaRT.anchorMax = new Vector2(1, 1);
            handleAreaRT.offsetMin = new Vector2(ui.Sx(8), 0); handleAreaRT.offsetMax = new Vector2(-ui.Sx(8), 0);
            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(handleArea.transform, false);
            var handleRT = (RectTransform)handle.transform;
            handleRT.sizeDelta = new Vector2(ui.Sx(22), ui.Sx(22));
            var handleImg = handle.GetComponent<Image>();
            handleImg.color = new Color(0.16f, 0.5f, 0.95f, 0f); // hidden until first interaction
            slider.targetGraphic = handleImg;
            slider.handleRect = handleRT;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min; slider.maxValue = max; slider.wholeNumbers = true; slider.value = min;

            if (!string.IsNullOrEmpty(maxLabel)) ui.BuildPxLabel((RectTransform)row.transform, maxLabel, ui.Sx(70), TextAlignmentOptions.Left, 12);

            // Value readout — fixed width so values 0..100 don't shift the row.
            var valueLabel = ui.BuildPxLabel((RectTransform)row.transform, "—", ui.Sx(36), TextAlignmentOptions.Center, 16);
            valueLabel.fontStyle = FontStyles.Bold;

            bool interacted = false;
            slider.onValueChanged.AddListener(v => {
                int iv = Mathf.RoundToInt(v);
                if (!interacted) {
                    interacted = true;
                    var c = handleImg.color; handleImg.color = new Color(c.r, c.g, c.b, 1f);
                }
                valueLabel.text = iv.ToString();
                SetAt(path, new Dictionary<string, object> { ["score"] = iv });
                UpdateSubmitInteractable();
            });
            leafChecks.Add(() => interacted);
        }

        void BuildMatrix(JToken item, List<string> path, RectTransform parent) {
            var title = item["title"]?.ToString();
            var instr = item["instruction"]?.ToString();
            var rows = item["rows"] as JArray;
            var options = item["options"] as JArray;
            if (rows == null || options == null) return;

            var panel = ui.BuildPanel(parent, "Matrix");
            if (!string.IsNullOrEmpty(title)) ui.BuildLabel(panel, title, 20, FontStyles.Bold);
            if (!string.IsNullOrEmpty(instr)) ui.BuildLabel(panel, instr, 14, FontStyles.Normal, new Color(0.4f, 0.4f, 0.4f));

            // Each row is HorizontalLayoutGroup [ fixed-width label : GridLayoutGroup ].
            // Same labelColW + same cellW * optCount across header & every row,
            // so columns line up by construction. labelColW is sized to fit the
            // longest row label (rough char-width estimate) rather than a fixed
            // fraction, so the cells sit close to the labels — no big gap.
            int optCount = options.Count;
            float panelInnerW = Mathf.Max(1, formContainer.rect.width - 2 * ui.Sx(12) - 2 * ui.Sx(8));
            int spacing = ui.Sx(4);
            int longestLabelChars = 0;
            foreach (var r in rows) {
                int n = (r["label"]?.ToString() ?? "").Length;
                if (n > longestLabelChars) longestLabelChars = n;
            }
            // ~0.55em per char at our 14pt label, plus a little breathing room.
            int labelColW = Mathf.Clamp(ui.Sx(Mathf.RoundToInt(longestLabelChars * 0.55f * 14f) + 16),
                                        ui.Sx(60), Mathf.RoundToInt(panelInnerW * 0.45f));
            int cellW = Mathf.Max(ui.Sx(28), Mathf.RoundToInt((panelInnerW - labelColW - spacing * (optCount + 1)) / Mathf.Max(1, optCount)));
            int cellH = ui.Sx(36);
            BuildMatrixRows(panel, options, rows, path, labelColW, cellW, cellH, spacing);
        }

        void BuildMatrixRows(RectTransform panel, JArray options, JArray rows, List<string> path, int labelColW, int cellW, int cellH, int spacing) {
            // Header
            var header = NewMatrixRow(panel, "Header", labelColW, cellH, spacing);
            ui.BuildPxLabel((RectTransform)header.transform, "", labelColW, TextAlignmentOptions.Left, 14);
            var headerGrid = NewMatrixGrid(header, options.Count, cellW, cellH, spacing);
            for (int i = 0; i < options.Count; i++) {
                ui.BuildPxLabel((RectTransform)headerGrid.transform, options[i].ToString(), cellW, TextAlignmentOptions.Center, 13);
            }

            var rowSelections = new Dictionary<string, int?>();
            foreach (var rowToken in rows) {
                var rowKey = rowToken["key"]?.ToString();
                var rowLabel = rowToken["label"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(rowKey)) continue;
                rowSelections[rowKey] = null;

                var rowRT = NewMatrixRow(panel, "Row_" + rowKey, labelColW, cellH, spacing);
                ui.BuildPxLabel((RectTransform)rowRT.transform, rowLabel, labelColW, TextAlignmentOptions.Left, 14);
                var grid = NewMatrixGrid(rowRT, options.Count, cellW, cellH, spacing);

                var cellImgs = new List<Image>();
                for (int i = 0; i < options.Count; i++) {
                    var cellGo = new GameObject($"Cell_{i}", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(PressDownButton));
                    cellGo.transform.SetParent(grid.transform, false);
                    var img = cellGo.GetComponent<Image>();
                    img.color = Color.white;
                    cellImgs.Add(img);
                    cellGo.GetComponent<Outline>().effectColor = new Color(0.7f, 0.7f, 0.7f);
                    var btn = cellGo.GetComponent<PressDownButton>();
                    int idx = i;
                    string keyCap = rowKey;
                    btn.onPress = () => {
                        rowSelections[keyCap] = idx;
                        SetAt(new List<string>(path) { keyCap }, idx);
                        for (int j = 0; j < cellImgs.Count; j++) {
                            cellImgs[j].color = (j == idx) ? new Color(0.16f, 0.5f, 0.95f) : Color.white;
                        }
                        UpdateSubmitInteractable();
                    };
                }
            }
            leafChecks.Add(() => rowSelections.Values.All(v => v != null));
        }

        // One matrix row = HorizontalLayoutGroup [ label : grid ]. childControl*
        // must be true so LayoutElement min/preferred sizes are honored; without
        // it children fall back to default 100×100 sizeDelta and the row's
        // visible height balloons. childForceExpandHeight=true makes the grid
        // fill the row height so cells aren't islands of cellH inside a tall
        // row, and childForceExpandWidth=false keeps widths from LayoutElement.
        RectTransform NewMatrixRow(RectTransform parent, string name, int labelColW, int cellH, int spacing) {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var hg = go.AddComponent<HorizontalLayoutGroup>();
            hg.spacing = spacing; hg.childAlignment = TextAnchor.MiddleLeft;
            hg.childForceExpandWidth = false; hg.childForceExpandHeight = true;
            hg.childControlWidth = true; hg.childControlHeight = true;
            hg.padding = new RectOffset(0, 0, ui.Sx(2), ui.Sx(2));
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = cellH; le.preferredHeight = cellH; le.flexibleHeight = 0;
            return (RectTransform)go.transform;
        }

        RectTransform NewMatrixGrid(RectTransform row, int columnCount, int cellW, int cellH, int spacing) {
            var go = new GameObject("Grid", typeof(RectTransform));
            go.transform.SetParent(row, false);
            var grid = go.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(cellW, cellH);
            grid.spacing = new Vector2(spacing, 0);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = columnCount;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.MiddleLeft;
            int totalW = columnCount * cellW + (columnCount - 1) * spacing;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = totalW; le.preferredWidth = totalW; le.flexibleWidth = 0;
            le.minHeight = cellH; le.preferredHeight = cellH; le.flexibleHeight = 0;
            return (RectTransform)go.transform;
        }

        void BuildSubmitButton(RectTransform parent) {
            var btnGo = new GameObject("Submit", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(parent, false);
            btnGo.GetComponent<Image>().color = new Color(0.16f, 0.5f, 0.95f);
            submitButton = btnGo.GetComponent<Button>();
            submitButton.onClick.AddListener(() => {
                if (debugMode) {
                    QoeLog.Event("rating", $"debug submit: {JsonConvert.SerializeObject(answers)}");
                    ClearForm();
                    SetStatus("Debug submit logged — form cleared");
                } else {
                    StartCoroutine(PostRating());
                }
            });
            var le = btnGo.AddComponent<LayoutElement>();
            le.minHeight = ui.Sx(60); le.preferredHeight = ui.Sx(60);

            var lbl = ui.BuildLabel((RectTransform)btnGo.transform, debugMode ? "Submit (debug, log only)" : "Submit Rating", 18, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            QoeUI.StretchToParent((RectTransform)lbl.transform);
        }

        IEnumerator PostRating() {
            if (string.IsNullOrEmpty(activeSid)) {
                QoeLog.Warn("rating", "PostRating with empty sid — ignoring");
                yield break;
            }
            if (submitButton != null) submitButton.interactable = false;
            SetStatus("Submitting…");
            var url = $"{HttpBase}/ratings";
            var body = JsonConvert.SerializeObject(new {
                sid = activeSid,
                condition_run_id = activeRunId,
                rating_type = "composite",
                data = answers,
            });
            using (var req = new UnityWebRequest(url, "POST")) {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                yield return req.SendWebRequest();
                bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode >= 200 && req.responseCode < 300;
                if (ok) {
                    QoeLog.Event("rating", $"submitted run {activeRunId}");
                    ClearForm();
                    SetStatus("Rating submitted — waiting for next request…");
                } else {
                    QoeLog.Warn("rating", $"submit failed: HTTP {req.responseCode} {req.error}");
                    if (submitButton != null) submitButton.interactable = true;
                    SetStatus($"Submit failed (HTTP {req.responseCode})");
                }
            }
        }

        // ── Answer tree helpers ──────────────────────────────────────────
        void SetAt(List<string> path, object value) {
            if (path.Count == 0) return;
            var cursor = answers;
            for (int i = 0; i < path.Count - 1; i++) {
                if (!cursor.TryGetValue(path[i], out var existing) || !(existing is Dictionary<string, object> child)) {
                    child = new Dictionary<string, object>();
                    cursor[path[i]] = child;
                }
                cursor = child;
            }
            cursor[path[path.Count - 1]] = value;
        }

        void UpdateSubmitInteractable() {
            if (submitButton == null) return;
            submitButton.interactable = leafChecks.All(c => c());
        }

        void SetStatus(string s) {
            if (statusText != null) statusText.text = s;
        }

        // ── Debug preview payload ────────────────────────────────────────
        // Mirrors RatingClientPage.tsx 'All combined' debug preview so we can
        // exercise every renderer (radio_scale, slider_scale, group, matrix)
        // in Unity without bringing the desktop server up.
        const string DEBUG_PREVIEW_JSON = @"[
            { ""id"": ""acr_0"", ""render"": ""radio_scale"", ""question"": ""Rate the overall quality of experience"", ""scale_min"": 1, ""scale_max"": 5, ""point_labels"": [""Bad"", ""Poor"", ""Fair"", ""Good"", ""Excellent""] },
            {
                ""id"": ""nasa_tlx"", ""render"": ""group"", ""title"": ""NASA Task Load Index"",
                ""instruction"": ""Rate each dimension from 0 (Very Low) to 100 (Very High). For Performance: 0 = Perfect, 100 = Failure."",
                ""items"": [
                    { ""id"": ""mental_demand"",   ""render"": ""slider_scale"", ""question"": ""Mental Demand"",   ""scale_min"": 0, ""scale_max"": 100, ""min_label"": ""Very Low"", ""max_label"": ""Very High"" },
                    { ""id"": ""physical_demand"", ""render"": ""slider_scale"", ""question"": ""Physical Demand"", ""scale_min"": 0, ""scale_max"": 100, ""min_label"": ""Very Low"", ""max_label"": ""Very High"" },
                    { ""id"": ""temporal_demand"", ""render"": ""slider_scale"", ""question"": ""Temporal Demand"", ""scale_min"": 0, ""scale_max"": 100, ""min_label"": ""Very Low"", ""max_label"": ""Very High"" },
                    { ""id"": ""performance"",     ""render"": ""slider_scale"", ""question"": ""Performance"",     ""scale_min"": 0, ""scale_max"": 100, ""min_label"": ""Perfect"",  ""max_label"": ""Failure"" },
                    { ""id"": ""effort"",          ""render"": ""slider_scale"", ""question"": ""Effort"",          ""scale_min"": 0, ""scale_max"": 100, ""min_label"": ""Very Low"", ""max_label"": ""Very High"" },
                    { ""id"": ""frustration"",     ""render"": ""slider_scale"", ""question"": ""Frustration"",     ""scale_min"": 0, ""scale_max"": 100, ""min_label"": ""Very Low"", ""max_label"": ""Very High"" }
                ]
            },
            {
                ""id"": ""ssq"", ""render"": ""matrix"", ""title"": ""Simulator Sickness Questionnaire"",
                ""instruction"": ""Rate each symptom on a scale of severity."",
                ""rows"": [
                    { ""key"": ""nausea"",     ""label"": ""Nausea"" },
                    { ""key"": ""eye_strain"", ""label"": ""Eye strain"" },
                    { ""key"": ""headache"",   ""label"": ""Headache"" }
                ],
                ""options"": [""None"", ""Slight"", ""Moderate"", ""Severe""]
            }
        ]";
    }
}
