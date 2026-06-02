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

        [Tooltip("How quickly the panel catches up to the camera. Higher = snappier.")]
        public float positionDamping = 6f;

        [Tooltip("How quickly the panel rotates to face the camera. Higher = snappier.")]
        public float rotationDamping = 8f;

        void Start() {
            ResolveMainCameraIfMissing();
            if (cam != null) {
                transform.position = cam.position + cam.forward * distance;
                var lookDir = transform.position - cam.position;
                if (lookDir.sqrMagnitude > 1e-6f)
                    transform.rotation = Quaternion.LookRotation(lookDir);
            }
        }

        public void ResolveMainCameraIfMissing() {
            if (cam != null && cam) return;
            if (Camera.main != null) cam = Camera.main.transform;
        }

        void LateUpdate() {
            if (cam == null || !cam) return;

            Vector3 target = cam.position + cam.forward * distance;
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
