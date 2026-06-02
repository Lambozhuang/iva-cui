using System.Collections;
using UnityEngine;

namespace QoeDevice {
    /// <summary>
    /// VR comfort fade. Draws a solid quad locked to the camera and ramps its
    /// alpha, so heavy main-thread work (additive scene activation) can be
    /// hidden behind black.
    ///
    /// Why this and not a loading scene / UGUI overlay:
    ///   • The scene-activation freeze is a MAIN-THREAD hitch — every frame
    ///     stops, including any spinner. You can't animate through it. What you
    ///     CAN do is make the LAST FRAME SUBMITTED before the hitch solid black,
    ///     so the headset's compositor reprojects black (comfortable) instead of
    ///     a frozen, head-tracked world (the nausea trigger). FadeOut() does not
    ///     return until that black frame has actually been presented.
    ///   • The quad is parented to the camera, so it stays glued to the view
    ///     under reprojection during the freeze — no sliding.
    ///   • A mesh quad with a ZTest Always shader covers controllers/hands/near
    ///     geometry that a world-space or depth-tested UGUI image would let poke
    ///     through.
    ///
    /// Usage (caller composes; all coroutines use unscaled time):
    ///     yield return fader.FadeOut(0.3f);   // world → black, black is on-screen on return
    ///     // ... heavy load / scene activation here ...
    ///     yield return fader.FadeIn(0.3f);    // black → world
    ///
    /// The fader is camera-agnostic: it binds to the camera passed to FadeOut
    /// (or Camera.main if none), and FadeIn reuses that same camera so a rig
    /// swap mid-load can't strand the quad on a destroyed transform.
    /// </summary>
    public class ScreenFader : MonoBehaviour {
        [Tooltip("Metres in front of the camera the fade quad sits. Must be > near clip (~0.05 on Quest). 0.3 is safe.")]
        public float quadDistance = 0.3f;
        [Tooltip("Half-extent of the fade quad in metres at quadDistance. 2 → covers ~160° FOV, far more than any HMD.")]
        public float quadHalfSize = 2f;
        [Tooltip("Fade colour. Black is the comfortable default; the compositor reprojecting black during a freeze is unnoticeable.")]
        public Color fadeColor = Color.black;

        Camera boundCamera;
        Transform quad;            // child of the bound camera
        Material mat;
        MeshRenderer meshRenderer;
        float alpha;               // current fade alpha, 0 = clear, 1 = opaque
        Coroutine activeFade;
        WaitForEndOfFrame eof;

        static readonly int ColorId = Shader.PropertyToID("_Color");

        public bool IsFullyOpaque => alpha >= 0.999f;

        void Awake() {
            eof = new WaitForEndOfFrame();
        }

        // ── Public API ────────────────────────────────────────────────────

        /// World → black. On return, a fully-black frame has been presented to
        /// the compositor, so it is safe to start the work you want hidden.
        public IEnumerator FadeOut(float duration, Camera cam = null) {
            Bind(cam != null ? cam : ResolveCamera());
            yield return Run(targetAlpha: 1f, duration);
            // Guarantee black is actually on-screen before we return. Alpha was
            // set this frame; one WaitForEndOfFrame lets it render & present,
            // a second covers the compositor's double buffer.
            yield return eof;
            yield return eof;
        }

        /// Black → world, then disables the quad so it costs nothing when idle.
        public IEnumerator FadeIn(float duration) {
            if (boundCamera == null && quad == null) yield break; // nothing to fade
            yield return Run(targetAlpha: 0f, duration);
            SetRendererEnabled(false);
        }

        /// Snap to black with no animation (e.g. before the very first load).
        public void SetBlackImmediate(Camera cam = null) {
            Bind(cam != null ? cam : ResolveCamera());
            SetAlpha(1f);
            SetRendererEnabled(true);
        }

        /// Snap to clear with no animation.
        public void SetClearImmediate() {
            SetAlpha(0f);
            SetRendererEnabled(false);
        }

        // ── Internals ─────────────────────────────────────────────────────

        IEnumerator Run(float targetAlpha, float duration) {
            if (activeFade != null) StopCoroutine(activeFade);
            activeFade = StartCoroutine(Lerp(targetAlpha, duration));
            yield return activeFade;
            activeFade = null;
        }

        IEnumerator Lerp(float targetAlpha, float duration) {
            EnsureQuad();
            SetRendererEnabled(true);
            float start = alpha;
            if (duration <= 0f) { SetAlpha(targetAlpha); yield break; }
            float t = 0f;
            while (t < duration) {
                t += Time.unscaledDeltaTime;
                SetAlpha(Mathf.Lerp(start, targetAlpha, Mathf.Clamp01(t / duration)));
                yield return null;
            }
            SetAlpha(targetAlpha);
        }

        // Re-parent the quad onto a (possibly new) camera. Called at FadeOut
        // start so the caller can hand us the rig that will actually be on
        // screen; FadeIn does not re-bind, so a rig swap during the hidden
        // work can't move the quad onto a camera that's about to be destroyed.
        void Bind(Camera cam) {
            if (cam == null) {
                QoeLog.Warn("fade", "no camera to bind — fade will not be visible");
                return;
            }
            boundCamera = cam;
            EnsureQuad();
            if (quad == null) return; // shader missing — EnsureQuad already logged
            quad.SetParent(cam.transform, false);
            quad.localPosition = new Vector3(0f, 0f, quadDistance);
            quad.localRotation = Quaternion.identity;
            quad.localScale = Vector3.one;
        }

        Camera ResolveCamera() {
            if (boundCamera != null) return boundCamera;
            if (Camera.main != null) return Camera.main;
            return FindObjectOfType<Camera>();
        }

        void EnsureQuad() {
            if (quad != null) return;

            var shader = Resources.Load<Shader>("QoeScreenFade");
            if (shader == null) shader = Shader.Find("QoE/ScreenFade");
            if (shader == null) {
                QoeLog.Err("fade", "QoE/ScreenFade shader not found (expected at Assets/.../Resources/QoeScreenFade.shader) — fade disabled");
                return;
            }
            mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetColor(ColorId, new Color(fadeColor.r, fadeColor.g, fadeColor.b, 0f));

            var go = new GameObject("ScreenFadeQuad");
            go.hideFlags = HideFlags.HideAndDontSave;
            quad = go.transform;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = BuildQuadMesh(quadHalfSize);

            meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = mat;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            meshRenderer.allowOcclusionWhenDynamic = false;
            meshRenderer.enabled = false;
        }

        static Mesh BuildQuadMesh(float h) {
            var m = new Mesh { name = "ScreenFadeQuad" };
            m.vertices = new[] {
                new Vector3(-h, -h, 0f), new Vector3(h, -h, 0f),
                new Vector3(-h,  h, 0f), new Vector3(h,  h, 0f),
            };
            // Two triangles, both windings via Cull Off in the shader so we
            // don't care which way the quad faces.
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateBounds();
            return m;
        }

        void SetAlpha(float a) {
            alpha = Mathf.Clamp01(a);
            if (mat == null) return;
            var c = mat.GetColor(ColorId);
            c.a = alpha;
            mat.SetColor(ColorId, c);
        }

        void SetRendererEnabled(bool on) {
            if (meshRenderer != null) meshRenderer.enabled = on;
        }

        void OnDestroy() {
            if (mat != null) Destroy(mat);
            if (quad != null) Destroy(quad.gameObject);
        }
    }
}
