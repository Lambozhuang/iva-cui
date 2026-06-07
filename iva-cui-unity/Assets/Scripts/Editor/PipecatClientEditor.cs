using UnityEditor;
using UnityEngine;

// Custom Inspector for PipecatClient: renders the mic device as a dropdown of the
// machine's actual Microphone.devices instead of a free-text field.
[CustomEditor(typeof(PipecatClient))]
public class PipecatClientEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var client = (PipecatClient)target;

        // Draw everything except micDeviceName the normal way.
        DrawPropertiesExcluding(serializedObject, "m_Script", "micDeviceName");

        var micProp = serializedObject.FindProperty("micDeviceName");
        string[] devices = Microphone.devices;

        if (devices.Length == 0)
        {
            EditorGUILayout.HelpBox("No microphone devices detected.", MessageType.Warning);
        }
        else
        {
            // Options: "(default)" + each device. Empty string == default (device[0]).
            var options = new string[devices.Length + 1];
            options[0] = "(default — device[0])";
            for (int i = 0; i < devices.Length; i++) options[i + 1] = devices[i];

            int current = 0;
            if (!string.IsNullOrEmpty(micProp.stringValue))
            {
                int idx = System.Array.IndexOf(devices, micProp.stringValue);
                current = idx >= 0 ? idx + 1 : 0;
            }

            int picked = EditorGUILayout.Popup("Mic Device", current, options);
            micProp.stringValue = picked == 0 ? "" : devices[picked - 1];
        }

        serializedObject.ApplyModifiedProperties();
    }
}
