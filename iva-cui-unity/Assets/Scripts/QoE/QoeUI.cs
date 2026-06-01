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

        // Solid-color rectangle button with a centered TMP label and a
        // PressDownButton listener. Caller can post-tweak the LayoutElement
        // (minHeight, flexibleWidth, etc.) on the returned component.
        public PressDownButton BuildButton(RectTransform parent, string text, Color color, int fontSize, Action onPress) {
            var go = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(PressDownButton), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            var btn = go.GetComponent<PressDownButton>();
            btn.onPress = onPress;
            var lbl = BuildLabel((RectTransform)go.transform, text, fontSize, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
            StretchToParent((RectTransform)lbl.transform);
            return btn;
        }
    }

    /// Fires onPress on PointerDown rather than waiting for PointerUp/Click.
    /// Lets the user select inside a ScrollRect without their tap being eaten
    /// by scroll-drag detection that only resolves on release.
    public class PressDownButton : MonoBehaviour, IPointerDownHandler {
        public Action onPress;
        public bool interactable = true;
        public void OnPointerDown(PointerEventData _) {
            if (interactable) onPress?.Invoke();
        }
    }
}
