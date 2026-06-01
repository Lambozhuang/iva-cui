using UnityEngine;

namespace QoeDevice {
    /// <summary>
    /// Keeps a transform (typically a World Space canvas) hovering at a fixed
    /// distance in front of the camera, but lazily: small head turns leave the
    /// panel where it is; when the camera looks more than
    /// <see cref="angularDeadZoneDeg"/> away from the panel, the panel catches
    /// up with exponential damping. Always rotates to face the camera.
    /// </summary>
    public class LazyCameraFollow : MonoBehaviour {
        [Tooltip("Camera the panel tracks. If null, uses Camera.main on Start.")]
        public Transform cam;

        [Tooltip("Distance in meters the panel sits in front of the camera.")]
        public float distance = 1.5f;

        [Tooltip("Position smoothing rate. Higher = snappier; lower = lazier.")]
        public float positionDamping = 5f;

        [Tooltip("Rotation smoothing rate. Higher = snappier; lower = lazier.")]
        public float rotationDamping = 5f;

        [Tooltip("Dead zone in degrees. Panel stays put until camera turns past this, then catches up.")]
        public float angularDeadZoneDeg = 25f;

        bool catchingUp;

        void Start() {
            ResolveMainCameraIfMissing();
            if (cam != null) {
                // Snap into place on the first frame so we don't lerp from origin.
                transform.position = cam.position + cam.forward * distance;
                var lookDir = transform.position - cam.position;
                if (lookDir.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(lookDir);
            }
        }

        // Pick up Camera.main lazily. The shell scene may not have a camera yet
        // when this component first runs (rig is in the additively-loaded task
        // scene, or — in our case — the shell rig is disabled while the task
        // scene's rig owns Camera.main). QoeDeviceClient calls this whenever
        // the active rig changes so the panel re-anchors to the live camera.
        public void ResolveMainCameraIfMissing() {
            if (cam != null && cam) return;
            if (Camera.main != null) cam = Camera.main.transform;
        }

        void LateUpdate() {
            // `cam == null` is true for both a never-assigned Transform and a
            // Unity-destroyed one. The explicit `!cam` covers the latter case
            // when an additive scene that owned Camera.main was unloaded.
            if (cam == null || !cam) return;

            Vector3 target = cam.position + cam.forward * distance;
            Vector3 toCanvas = transform.position - cam.position;
            Vector3 toCanvasDir = toCanvas.sqrMagnitude > 1e-6f ? toCanvas.normalized : cam.forward;
            float angle = Vector3.Angle(toCanvasDir, cam.forward);

            if (!catchingUp && angle > angularDeadZoneDeg) catchingUp = true;
            if (catchingUp) {
                float pt = 1f - Mathf.Exp(-positionDamping * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, target, pt);
                Vector3 after = transform.position - cam.position;
                if (after.sqrMagnitude > 1e-6f && Vector3.Angle(after.normalized, cam.forward) < 1f)
                    catchingUp = false;
            }

            Vector3 face = transform.position - cam.position;
            if (face.sqrMagnitude > 1e-6f) {
                Quaternion targetRot = Quaternion.LookRotation(face);
                float rt = 1f - Mathf.Exp(-rotationDamping * Time.deltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rt);
            }
        }
    }
}
