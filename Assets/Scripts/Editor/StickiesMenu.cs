using Firefighting.Haptics.Stickies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Editor shortcuts for the Sensorimotor Stickies wall-proximity feature.</summary>
public static class StickiesMenu
{
    [MenuItem("Tools/Stickies/Add Wall Proximity Haptics to Scene")]
    private static void AddToScene()
    {
        WallProximityHaptics existing = Object.FindAnyObjectByType<WallProximityHaptics>(FindObjectsInactive.Include);
        if (existing != null)
        {
            Selection.activeObject = existing.gameObject;
            EditorGUIUtility.PingObject(existing.gameObject);
            Debug.Log("[Stickies] This scene already has Wall Proximity Haptics; selected it.", existing);
            return;
        }

        var host = new GameObject("Stickies Wall Haptics");
        Undo.RegisterCreatedObjectUndo(host, "Add Wall Proximity Haptics");
        Undo.AddComponent<WallProximityHaptics>(host);
        Selection.activeObject = host;
        EditorSceneManager.MarkSceneDirty(host.scene);
    }

    [MenuItem("Tools/Stickies/Open Stickies BLE Docs")]
    private static void OpenDocs() => Application.OpenURL("https://stickies.catherineyu.com/docs/develop/ble-api");
}
