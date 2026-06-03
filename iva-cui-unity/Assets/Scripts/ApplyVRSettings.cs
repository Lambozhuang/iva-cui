using StarterAssets;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

public class ApplyVRSettings : MonoBehaviour
{
    private string questMicString = "Headset Microphone (Oculus Virtual Audio Device)";

    [Tooltip("Disable all player locomotion (move, turn, teleport, grab-move, climb, and the desktop WASD controller) at startup. The QoE study teleports the subject by code, so free locomotion must stay off at all times.")]
    [SerializeField] private bool lockLocomotion = true;

    [SerializeField] private GameObject EyeTrackingObject;

    private XROrigin player;
    private Camera playerHead;

    private float referenceHeightM = 0.0f;
    private Vector3 currentHMDPosition;

    private void Start()
    {
        if (lockLocomotion)
        {
            DisableAllLocomotion();
        }

        var xrOrigin = FindObjectOfType<XROrigin>();
        if (xrOrigin == null) { return; }

        if (xrOrigin.isActiveAndEnabled)
        {
            Debug.Log("Applying VR settings");
            ApplySettings();
        }

        player = FindObjectOfType<XROrigin>();
        playerHead = Camera.main;
    }

    // Turns off every locomotion source so the subject can neither move nor
    // turn — the study teleports them by code (QoeDeviceClient.TeleportToTask),
    // and free locomotion would let them wander off the spawn or reorient away
    // from the agent. All XRI providers (Move, Snap/Continuous Turn, Teleport,
    // Grab Move, Climb) share the LocomotionProvider base, so disabling that
    // component covers them in one sweep; the desktop WASD rig uses
    // FirstPersonController instead. Inactive objects are included so the
    // currently-unused rig (XR vs WASD) is locked too, and so dormant providers
    // can't be re-activated into a live mover. Code teleports move the rig
    // transform directly and are unaffected.
    private void DisableAllLocomotion()
    {
        int n = 0;
        foreach (var provider in FindObjectsByType<LocomotionProvider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            provider.enabled = false;
            n++;
        }
        foreach (var fps in FindObjectsByType<FirstPersonController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            fps.enabled = false;
            n++;
        }
        Debug.Log($"Locomotion locked — disabled {n} locomotion component(s).");
    }

    private void ApplySettings()
    {
        MicrophoneHandler microphoneHandler = FindObjectOfType<MicrophoneHandler>();
        if (microphoneHandler == null)
        {
            Debug.LogError("MicrophoneHandler not found when applying VR Settings");
            return;
        }

        // Only force the Quest mic when it's actually present (i.e. running on the
        // headset). On PC / Meta XR Simulator that device doesn't exist, so leave
        // the Inspector-selected mic alone instead of clobbering it and falling
        // back to an unintended default.
        if (System.Array.IndexOf(Microphone.devices, questMicString) >= 0)
        {
            microphoneHandler.selectedMicString = questMicString;
        }
        else
        {
            Debug.Log($"Quest mic '{questMicString}' not present — keeping selected mic '{microphoneHandler.selectedMicString}'.");
        }

        if (EyeTrackingObject != null)
        {
            EyeTrackingObject.SetActive(true);
        }
    }

    private void Update()
    {
        // Record the standing height on pressing 'T'
        if (Input.GetKeyDown(KeyCode.T))
        {
            referenceHeightM = playerHead.transform.position.y;
            Debug.Log($"Recorded Reference Height: {referenceHeightM}");
        }

        // Reset the origin on pressing 'R'
        if (Input.GetKeyDown(KeyCode.R))
        {
            var dynamicMoveProvider = FindObjectOfType<DynamicMoveProvider>();
            if (dynamicMoveProvider == null)
            {
                Debug.LogError("IT'S OVER 9000!!! (DynamicMoveProvider not found)");
                return;
            }
            dynamicMoveProvider.useGravity = false;
            dynamicMoveProvider.gravityApplicationMode = DynamicMoveProvider.GravityApplicationMode.AttemptingMove;
            ResetOrigin();
        }
    }

    private void ResetOrigin()
    {
        if (!Application.IsPlaying(gameObject))
            return;

        currentHMDPosition = playerHead.transform.position;
        float deltaY = currentHMDPosition.y - referenceHeightM;

        player.transform.position -= new Vector3(0, deltaY, 0);

        Debug.Log($"Player Position Adjusted: {player.transform.position}");
    }
}