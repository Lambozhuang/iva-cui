using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QoeDevice {
    /// <summary>
    /// Shared UGUI builders used by QoeRatingClient and QoeDeviceClient. All
    /// authored sizes are in *reference* pixels (e.g. font 14, panel padding
    /// 12) and the helper multiplies them by <see cref="scale"/> at build
    /// time. Caller sets <c>scale = container.rect.width / referenceWidth</c>
    /// before invoking the builders.
    /// </summary>
    public class QoeUI {
        public float scale = 1f;

        public int Sx(float v) => Mathf.Max(1, Mathf.RoundToInt(v * scale));

        public RectTransform BuildPanel(RectTransform parent, string name) {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.98f, 0.98f, 0.98f);
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = Sx(6);
            vlg.padding = new RectOffset(Sx(12), Sx(12), Sx(10), Sx(10));
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            var csf = go.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return (RectTransform)go.transform;
        }

        public TMP_Text BuildLabel(RectTransform parent, string text, int size, FontStyles style, Color? color = null, TextAlignmentOptions align = TextAlignmentOptions.Left) {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = Sx(size);
            tmp.fontStyle = style;
            tmp.color = color ?? Color.black;
            tmp.alignment = align;
            tmp.enableWordWrapping = true;
            return tmp;
        }

        // Fixed-pixel-width label. widthPx is in *physical* pixels (already
        // scaled). Caller wraps reference values in Sx(...) where needed.
        public TMP_Text BuildPxLabel(RectTransform parent, string text, int widthPx, TextAlignmentOptions align, int size) {
            var go = new GameObject("PxLabel", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = Sx(size);
            tmp.alignment = align;
            tmp.color = new Color(0.3f, 0.3f, 0.3f);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = Sx(Mathf.Max(6, size - 4));
            tmp.fontSizeMax = Sx(size);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = widthPx; le.preferredWidth = widthPx; le.flexibleWidth = 0;
            le.minHeight = 0; le.preferredHeight = Sx(20); le.flexibleHeight = 1;
            return tmp;
        }

        // ScrollRect viewport fills the parent. Returns the Content rect that
        // grows downward inside a vertical layout — caller adds children to it.
        public RectTransform BuildScrollRect(RectTransform parent) {
            var srGo = new GameObject("ScrollRect", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            srGo.transform.SetParent(parent, false);
            var srRT = (RectTransform)srGo.transform;
            srRT.anchorMin = Vector2.zero; srRT.anchorMax = Vector2.one;
            srRT.offsetMin = Vector2.zero; srRT.offsetMax = Vector2.zero;
            srGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.02f);

            var viewportGo = new GameObject("Viewport", typeof(RectTransform));
            viewportGo.transform.SetParent(srGo.transform, false);
            var viewportRT = (RectTransform)viewportGo.transform;
            viewportRT.anchorMin = Vector2.zero; viewportRT.anchorMax = Vector2.one;
            viewportRT.offsetMin = Vector2.zero; viewportRT.offsetMax = Vector2.zero;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRT = (RectTransform)contentGo.transform;
            contentRT.anchorMin = new Vector2(0, 1); contentRT.anchorMax = new Vector2(1, 1);
            contentRT.pivot = new Vector2(0.5f, 1f);
            contentRT.offsetMin = Vector2.zero; contentRT.offsetMax = Vector2.zero;
            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = Sx(10);
            vlg.padding = new RectOffset(Sx(8), Sx(8), Sx(8), Sx(8));
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            var csf = contentGo.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var sr = srGo.GetComponent<ScrollRect>();
            sr.viewport = viewportRT;
            sr.content = contentRT;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;

            return contentRT;
        }

        public static void StretchToParent(RectTransform rt) {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // Empty RectTransform anchored to a fractional sub-rect of its parent
        // (e.g. anchorMin/Max = (0,0.8)..(0.5,1) → top-left fifth). insetPx
        // (already scaled) shrinks it inward on all sides so neighbouring corner
        // clusters don't touch. Used to carve the canvas into corner regions.
        public RectTransform BuildAnchoredRegion(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, int insetPx = 0) {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = new Vector2(insetPx, insetPx);
            rt.offsetMax = new Vector2(-insetPx, -insetPx);
            return rt;
        }

        // Solid-color rectangle button with a centered TMP label and a
        // PressDownButton listener. Caller can post-tweak the LayoutElement
        // (minHeight, flexibleWidth, etc.) on the returned component. The
        // PressDownButton owns its fill color from here on — pass the resting
        // color in and it handles the controller-hover tint and disabled dim.
        public PressDownButton BuildButton(RectTransform parent, string text, Color color, int fontSize, Action onPress) {
            var go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(PressDownButton), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var btn = go.GetComponent<PressDownButton>();
            btn.SetNormalColor(color);
            btn.onPress = onPress;
            var lbl = BuildLabel((RectTransform)go.transform, text, fontSize, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            StretchToParent((RectTransform)lbl.transform);
            return btn;
        }
    }

    /// Fires onPress on PointerDown rather than waiting for PointerUp/Click.
    /// Lets the user select inside a ScrollRect without their tap being eaten
    /// by scroll-drag detection that only resolves on release.
    ///
    /// Owns the backing Image's fill color so the three visual states stay in
    /// one place: resting (<see cref="SetNormalColor"/>), hovered (the
    /// controller's UI ray is over it — fires via IPointerEnter/Exit from the
    /// canvas's TrackedDeviceGraphicRaycaster), and disabled (dimmed). Pointer
    /// enter/exit bubble up from the child label, so hover works even though the
    /// TMP label is the actual raycast target.
    public class PressDownButton : MonoBehaviour, IPointerDownHandler, IPointerEnterHandler, IPointerExitHandler {
        public Action onPress;

        // How far the hover tint pushes the resting color, and how much the
        // disabled state fades it (0.35 matches the dim QoeDeviceClient used to
        // apply by hand). Hover brightens dark/colored fills and slightly darkens
        // light ones, so the highlight reads on every button — including the
        // white rating cells.
        const float kHoverLerp = 0.20f;
        const float kDisabledAlpha = 0.35f;

        Color normalColor = Color.white;
        Image img;
        bool hovered;
        bool _interactable = true;

        public bool interactable {
            get => _interactable;
            set {
                if (_interactable == value) return;
                _interactable = value;
                // Drop any stale hover so a disabled-then-re-enabled button
                // doesn't light up until the ray actually re-enters it.
                if (!value) hovered = false;
                RefreshColor();
            }
        }

        // Sets the resting fill. Callers that recolor a button (e.g. a rating
        // cell going selected→blue) go through here so hover/disabled compose on
        // top of the new base instead of fighting a directly-set Image color.
        public void SetNormalColor(Color c) {
            normalColor = c;
            RefreshColor();
        }

        public void OnPointerEnter(PointerEventData _) { hovered = true;  RefreshColor(); }
        public void OnPointerExit (PointerEventData _) { hovered = false; RefreshColor(); }

        public void OnPointerDown(PointerEventData _) {
            if (interactable) onPress?.Invoke();
        }

        void RefreshColor() {
            if (img == null) img = GetComponent<Image>();
            if (img == null) return;
            Color c = (hovered && _interactable) ? HoverTint(normalColor) : normalColor;
            if (!_interactable) c.a *= kDisabledAlpha;
            img.color = c;
        }

        static Color HoverTint(Color c) {
            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            return lum > 0.6f ? Color.Lerp(c, Color.black, kHoverLerp * 0.5f)
                              : Color.Lerp(c, Color.white, kHoverLerp);
        }
    }
}
