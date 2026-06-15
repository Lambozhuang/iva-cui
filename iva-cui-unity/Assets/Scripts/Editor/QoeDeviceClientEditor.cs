using UnityEditor;
using UnityEngine;
using QoeDevice;

// Custom Inspector for QoeDeviceClient: adds buttons to build the subject-facing
// HUD prompt card in edit mode (no Play, no connect/briefing flow) so its layout
// can be checked for text overflow straight from the inspector. The build logic
// lives on the component (EditorPreviewPromptCard / EditorClearPreview, both
// UNITY_EDITOR-only); this just exposes it as buttons.
[CustomEditor(typeof(QoeDeviceClient))]
public class QoeDeviceClientEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var client = (QoeDeviceClient)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("HUD prompt-card preview (edit mode)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Builds only the subject's prompt card into rootContainer, filled with the " +
            "worst-case text, so you can spot overflow without running the study. " +
            "Clear it (or press Play, which rebuilds the whole UI) when done.",
            MessageType.None);

        using (new EditorGUI.DisabledScope(Application.isPlaying || client.rootContainer == null))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview prompt card"))
                    client.EditorPreviewPromptCard();
                if (GUILayout.Button("Clear preview"))
                    client.EditorClearPreview();
            }
        }

        if (client.rootContainer == null)
            EditorGUILayout.HelpBox("Assign rootContainer to enable the preview.", MessageType.Warning);
    }
}
