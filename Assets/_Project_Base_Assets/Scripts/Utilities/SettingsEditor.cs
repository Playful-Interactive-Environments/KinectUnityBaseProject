#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(Settings))]
public class SettingsEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        Settings settings = (Settings)target;

        EditorGUILayout.Space(10);

        using (new EditorGUI.DisabledScope(!Application.isPlaying || SourceManager.instance == null))
        {
            if (GUILayout.Button("Set All Bounds to Current Image Size"))
            {
                int w = SourceManager.instance.SourceWidth;
                int h = SourceManager.instance.SourceHeight;

                Undo.RecordObject(settings, "Set Bounds to Current Resolution");

                // Call a new method that updates all three properties
                settings.SetAllBoundsToResolution(w, h);

                EditorUtility.SetDirty(settings);
            }
        }

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to read the current source resolution.", MessageType.Info);
        }
    }
}
#endif