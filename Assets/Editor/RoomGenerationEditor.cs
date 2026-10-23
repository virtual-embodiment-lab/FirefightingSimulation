using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(RoomGeneration))]
public class RoomGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        RoomGeneration generator = (RoomGeneration)target;
        GUILayout.Space(10);

        if (GUILayout.Button("Generate Rooms", GUILayout.Height(40)))
        {
            generator.GenerateAllRooms();
        }

        // Generating a house detaches any old occlusion bake, so bake again once the layout is final (Quest relies on it)
        GUILayout.Space(5);
        using (new EditorGUI.DisabledScope(StaticOcclusionCulling.isRunning || EditorApplication.isPlaying))
        {
            if (GUILayout.Button(StaticOcclusionCulling.isRunning ? "Baking Occlusion Culling..." : "Bake Occlusion Culling", GUILayout.Height(25)))
            {
                if (string.IsNullOrEmpty(generator.gameObject.scene.path))
                    EditorUtility.DisplayDialog("Save the scene first", "Occlusion data is saved next to the scene, so save the scene before baking.", "OK");
                else
                    StaticOcclusionCulling.GenerateInBackground();
            }
        }

        if (!StaticOcclusionCulling.isRunning && StaticOcclusionCulling.umbraDataSize == 0)
            EditorGUILayout.HelpBox("No occlusion culling is baked for this scene. Bake once the house layout is final so standalone Quest builds can skip hidden geometry.", MessageType.Info);
    }
}
