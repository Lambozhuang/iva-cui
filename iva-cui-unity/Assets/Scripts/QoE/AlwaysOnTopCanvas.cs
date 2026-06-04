using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QoeDevice {
    /// <summary>
    /// Makes a world-space UI canvas draw on top of everything, so scene geometry
    /// between the subject and the HUD can't occlude it. VR HUDs float ~1 m in
    /// front of the camera (see <see cref="LazyCameraFollow"/>); anything the
    /// player walks up to, or any prop in the room, would otherwise punch a hole
    /// in the HUD.
    ///
    /// How: UGUI graphics normally render with ZTest LEqual, so they hide behind
    /// nearer geometry. This swaps every Image/Text material under the canvas for
    /// a ZTest-Always equivalent (the same trick <see cref="ScreenFader"/> uses
    /// for its comfort quad) and raises the canvas sortingOrder. ZTest Always is
    /// what actually defeats the occlusion; sortingOrder just keeps the HUD after
    /// other transparent draws.
    ///
    /// Self-healing: the QoE HUD is built (and rebuilt) at runtime, and the rating
    /// form spawns/destroys graphics on the fly. Rather than hook every rebuild,
    /// this re-checks each frame in LateUpdate and patches any graphic not yet on
    /// the overlay shader. The check is a cheap shader-equality test, idempotent,
    /// and skips already-patched graphics — worst case a one-frame flash on a
    /// brand-new element.
    ///
    /// Setup (Unity Editor — do this yourself): add this component to the HUD
    /// canvas root (the GameObject with the Canvas + LazyCameraFollow). No shader
    /// assignment is required — the overlay shaders are resolved by name and have
    /// been added to Always Included Shaders so they survive device builds. The
    /// optional Shader fields below are only there if you want to pin specific
    /// shaders explicitly.
    /// </summary>
    [DisallowMultipleComponent]
    public class AlwaysOnTopCanvas : MonoBehaviour {
        [Header("Sorting")]
        [Tooltip("Canvas sortingOrder applied at startup. Higher = drawn later. " +
                 "Only affects ordering among transparent draws; ZTest Always is " +
                 "what defeats geometry occlusion.")]
        public int sortingOrder = 100;

        [Header("Overlay shaders (optional — resolved by name if left empty)")]
        [Tooltip("ZTest-off shader for UI Images. Defaults to 'UI/NoZTest'.")]
        public Shader imageOverlayShader;
        [Tooltip("ZTest-always shader for TextMeshPro text. Defaults to " +
                 "'TextMeshPro/Distance Field Overlay'.")]
        public Shader textOverlayShader;

        // One shared overlay material for all Images: UGUI passes each Image's
        // sprite to the CanvasRenderer separately from the material, so a single
        // material is correct across images with different sprites.
        Material imageOverlayMat;
        // TMP keeps its glyph atlas in the material's _MainTex, so each distinct
        // font material needs its own overlay copy. Keyed by source material id.
        readonly Dictionary<int, Material> textOverlayMats = new();

        bool warnedNoImageShader;
        bool warnedNoTextShader;

        void OnEnable() {
            ApplySortingOrder();
            PatchAll();
        }

        // Re-patch every frame: cheap shader-equality checks, and it keeps newly
        // built UI (briefing panel, rating form) on the overlay shader without
        // having to hook each rebuild site.
        void LateUpdate() {
            PatchAll();
        }

        void ApplySortingOrder() {
            var canvas = GetComponent<Canvas>();
            if (canvas == null) canvas = GetComponentInChildren<Canvas>();
            if (canvas != null) {
                canvas.overrideSorting = true;
                canvas.sortingOrder = sortingOrder;
            }
        }

        void PatchAll() {
            PatchImages();
            PatchTexts();
        }

        void PatchImages() {
            var shader = ResolveImageShader();
            if (shader == null) return;
            if (imageOverlayMat == null || imageOverlayMat.shader != shader) {
                imageOverlayMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            // Includes RawImage; both derive from MaskableGraphic. TMP text is a
            // Graphic too but is handled separately (different shader/atlas), so
            // skip it here.
            var graphics = GetComponentsInChildren<Graphic>(true);
            foreach (var g in graphics) {
                if (g is TMP_Text) continue;
                if (g is Image || g is RawImage) {
                    if (g.material != imageOverlayMat) g.material = imageOverlayMat;
                }
            }
        }

        void PatchTexts() {
            var shader = ResolveTextShader();
            if (shader == null) return;

            var texts = GetComponentsInChildren<TMP_Text>(true);
            foreach (var t in texts) {
                var current = t.fontSharedMaterial;
                if (current == null) continue;
                if (current.shader == shader) continue; // already an overlay material

                int key = current.GetInstanceID();
                if (!textOverlayMats.TryGetValue(key, out var overlay) || overlay == null) {
                    // Copy preserves the glyph atlas (_MainTex) and all SDF/face
                    // properties; swapping the shader to the Overlay variant only
                    // changes the render state (ZTest Always) — matching property
                    // names carry over.
                    overlay = new Material(current) { hideFlags = HideFlags.HideAndDontSave };
                    overlay.shader = shader;
                    textOverlayMats[key] = overlay;
                }
                t.fontSharedMaterial = overlay;
            }
        }

        Shader ResolveImageShader() {
            if (imageOverlayShader != null) return imageOverlayShader;
            var s = Shader.Find("UI/NoZTest");
            if (s == null && !warnedNoImageShader) {
                QoeLog.Warn("hud", "UI/NoZTest shader not found — HUD images may be occluded by geometry");
                warnedNoImageShader = true;
            }
            return s;
        }

        Shader ResolveTextShader() {
            if (textOverlayShader != null) return textOverlayShader;
            var s = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (s == null) s = Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
            if (s == null && !warnedNoTextShader) {
                QoeLog.Warn("hud", "TextMeshPro overlay shader not found — HUD text may be occluded by geometry");
                warnedNoTextShader = true;
            }
            return s;
        }

        void OnDestroy() {
            if (imageOverlayMat != null) Destroy(imageOverlayMat);
            foreach (var m in textOverlayMats.Values)
                if (m != null) Destroy(m);
            textOverlayMats.Clear();
        }
    }
}
