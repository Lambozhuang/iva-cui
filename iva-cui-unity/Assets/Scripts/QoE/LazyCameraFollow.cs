using UnityEngine;

namespace QoeDevice {
    /// <summary>
    /// Keeps a world-space canvas hovering in front of the camera with smooth
    /// exponential damping. Always faces the camera.
    /// </summary>
    public class LazyCameraFollow : MonoBehaviour {
        [Tooltip("Camera to track. Falls back to Camera.main if null.")]
        public Transform cam;

        [Tooltip("Distance in metres the panel sits in front of the camera.")]
        public float distance = 1.5f;

        [Tooltip("Metres to drop the panel below the camera's eye line so it isn't " +
                 "right in the subject's face. Applied along world-down.")]
        public float verticalOffset = 0.35f;

        [Tooltip("How quickly the panel catches up to the camera. Higher = snappier.")]
        public float positionDamping = 6f;

        [Tooltip("How quickly the panel rotates to face the camera. Higher = snappier.")]
        public float rotationDamping = 8f;

        void Start() {
            ResolveMainCameraIfMissing();
            if (cam != null) {
                transform.position = TargetPosition();
                var lookDir = transform.position - cam.position;
                if (lookDir.sqrMagnitude > 1e-6f)
                    transform.rotation = Quaternion.LookRotation(lookDir);
            }
        }

        // In front of the camera, then dropped straight down by verticalOffset so
        // the panel sits below the subject's eye line rather than dead-center.
        Vector3 TargetPosition() {
            return cam.position + cam.forward * distance + Vector3.down * verticalOffset;
        }

        public void ResolveMainCameraIfMissing() {
            if (cam != null && cam) return;
            if (Camera.main != null) cam = Camera.main.transform;
        }

        void LateUpdate() {
            if (cam == null || !cam) return;

            Vector3 target = TargetPosition();
            float pt = 1f - Mathf.Exp(-positionDamping * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, target, pt);

            Vector3 face = transform.position - cam.position;
            if (face.sqrMagnitude > 1e-6f) {
                Quaternion targetRot = Quaternion.LookRotation(face);
                float rt = 1f - Mathf.Exp(-rotationDamping * Time.deltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rt);
            }
        }
    }
}
