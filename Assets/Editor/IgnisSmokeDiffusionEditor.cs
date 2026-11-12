// Previews a single Ignis smoke volume and marks its approximate height in the Scene view.
using Ignis;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;

[CustomEditor(typeof(IgnisSmokeDiffusion))]
public class IgnisSmokeDiffusionEditor : Editor
{
    private Transform previewAnchor;
    private Transform defaultAnchor;
    private VisualEffect preview;
    private float previewStrength = 1f;
    private bool showHeightMarkers = true;

    // Find an unambiguous automatic ignition source for scenes with an empty source list.
    private void OnEnable()
    {
        var smoke = (IgnisSmokeDiffusion)target;
        foreach (var fire in FindObjectsByType<FlammableObject>(FindObjectsSortMode.None))
        {
            if (fire.gameObject.scene != smoke.gameObject.scene ||
                !fire.setThisOnFireOnStart || !fire.enableFlameVFX ||
                (fire.overrideFireVFX && fire.overrideVFXVariant == FlameEngine.FireVFXVariant.OnlySmoke))
                continue;

            // If the scene has multiple candidates, let the user choose the location.
            if (defaultAnchor != null)
            {
                defaultAnchor = null;
                return;
            }

            defaultAnchor = fire.transform;
        }
    }

    private void OnDisable() => StopPreview();

    // Keep the runtime Inspector intact and expose controls for the temporary Scene view preview.
    public override void OnInspectorGUI()
    {
        var smoke = (IgnisSmokeDiffusion)target;
        DrawSmokeSettings(smoke);
        if (smoke.smokeTemplate != null)
            EditorGUILayout.HelpBox("Smoke Template supplies the emitter shape and appearance.", MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Edit Mode Preview", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode ||
                   EditorUtility.IsPersistent(smoke) || !smoke.isActiveAndEnabled))
        {
            EditorGUI.BeginChangeCheck();
            previewAnchor = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Preview Anchor", "Optional scene object to place the preview above. Leaving it empty uses the default fire source."),
                previewAnchor, typeof(Transform), true);
            previewStrength = EditorGUILayout.Slider(
                new GUIContent("Preview Strength", "Fraction of the authored smoke opacity."),
                previewStrength, 0f, 1f);
            showHeightMarkers = EditorGUILayout.Toggle("Show Height Markers", showHeightMarkers);
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();

            FlameEngine engine = smoke.GetComponent<FlameEngine>();
            VisualEffect prefab = GetSmokeEffect(engine);
            bool validAnchor = TryGetPositions(smoke, out _, out _);
            using (new EditorGUI.DisabledScope(preview == null && (!validAnchor || prefab == null)))
            {
                if (GUILayout.Button(preview == null ? "Preview Smoke Appearance" : "Stop Preview"))
                {
                    if (preview == null)
                        StartPreview(prefab, smoke.gameObject.scene);
                    else
                        StopPreview();
                }
            }

            if (!validAnchor)
                EditorGUILayout.HelpBox("Assign a preview anchor in this scene, such as the stove.", MessageType.Info);
            else if (prefab == null)
                EditorGUILayout.HelpBox("The FlameEngine on this object needs an OnlySmoke VFX prefab.", MessageType.Info);
        }
    }

    // Draw settings in their declared order and insert the field and warnings below the toggle.
    private void DrawSmokeSettings(IgnisSmokeDiffusion smoke)
    {
        serializedObject.Update();
        SerializedProperty property = serializedObject.GetIterator();
        for (bool enterChildren = true; property.NextVisible(enterChildren); enterChildren = false)
        {
            using (new EditorGUI.DisabledScope(property.propertyPath == "m_Script"))
                EditorGUILayout.PropertyField(property, true);

            if (property.propertyPath != nameof(smoke.useContainmentEnabledSmoke) || !property.boolValue)
                continue;

            SerializedProperty fieldProperty = serializedObject.FindProperty(nameof(smoke.bakedCollisionField));
            EditorGUILayout.PropertyField(fieldProperty);
            var field = fieldProperty.objectReferenceValue as SmokeContainmentField;
            FlameEngine engine = smoke.GetComponent<FlameEngine>();
            if (field == null || !field.IsValid)
                EditorGUILayout.HelpBox("Assign a valid baked collision field. Standard Ignis smoke is used until a field is assigned.", MessageType.Warning);
            else if (engine == null || engine.containmentSmokeGraph == null)
                EditorGUILayout.HelpBox("The FlameEngine needs the containment graph. Standard Ignis smoke is used until it is assigned.", MessageType.Warning);
        }
        serializedObject.ApplyModifiedProperties();
    }

    // Create only the graph instance, so previewing never starts the runtime fire lifecycle.
    private void StartPreview(VisualEffect prefab, Scene scene)
    {
        var previewObject = new GameObject("Ignis Smoke Appearance Preview");
        SceneManager.MoveGameObjectToScene(previewObject, scene);
        previewObject.hideFlags = HideFlags.HideAndDontSave;
        preview = previewObject.AddComponent<VisualEffect>();
        preview.visualEffectAsset = prefab.visualEffectAsset;
        preview.pause = false;
        EditorApplication.update += TickPreview;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
        EditorSceneManager.sceneClosing += OnSceneClosing;
        TickPreview();
        if (preview != null)
            preview.Reinit();
    }

    // Remove the preview and all callbacks before leaving its editing context.
    private void StopPreview()
    {
        EditorApplication.update -= TickPreview;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
        EditorSceneManager.sceneClosing -= OnSceneClosing;
        if (preview != null)
            DestroyImmediate(preview.gameObject);

        preview = null;
        SceneView.RepaintAll();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
            StopPreview();
    }

    private void OnSceneClosing(Scene scene, bool removingScene)
    {
        if (preview != null && preview.gameObject.scene == scene)
            StopPreview();
    }

    // Apply the same smoke inputs as the runtime emitter while the Inspector is being tuned.
    private void TickPreview()
    {
        var smoke = target as IgnisSmokeDiffusion;
        if (preview == null || smoke == null || !smoke.isActiveAndEnabled ||
            EditorApplication.isPlayingOrWillChangePlaymode ||
            !TryGetPositions(smoke, out Vector3 spawn, out _))
        {
            StopPreview();
            return;
        }

        FlameEngine engine = smoke.GetComponent<FlameEngine>();
        VisualEffect prefab = GetSmokeEffect(engine);
        if (prefab == null)
        {
            StopPreview();
            return;
        }

        SmokeContainmentField field = smoke.bakedCollisionField;
        bool useContainment = smoke.useContainmentEnabledSmoke && field != null &&
            field.IsValid && engine.containmentSmokeGraph != null;
        VisualEffectAsset graph = useContainment ? engine.containmentSmokeGraph : prefab.visualEffectAsset;
        bool graphChanged = preview.visualEffectAsset != graph;
        if (graphChanged)
            preview.visualEffectAsset = graph;
        if (useContainment)
            field.Bind(preview);

        FlammableObject template = smoke.smokeTemplate;
        Color color = template != null ? template.smokeColor : smoke.smokeColor;
        float intensity = template != null ? template.smokeColorIntensity : smoke.smokeColorIntensity;
        float alpha = template != null ? template.smokeAlpha : smoke.smokeAlpha;
        Vector3 size = smoke.generatedBlockScale;
        Vector3 center = spawn;
        Vector3 rotation = Vector3.zero;
        if (template != null)
        {
            size = template.transform.localScale;
            BoxCollider box = template.GetComponentInChildren<BoxCollider>(true);
            if (box != null)
            {
                Transform root = template.transform;
                Matrix4x4 boxToEmitter = Matrix4x4.Scale(size) * root.worldToLocalMatrix * box.transform.localToWorldMatrix;
                center += boxToEmitter.MultiplyPoint3x4(box.center);
                size = Vector3.Scale(box.size, boxToEmitter.lossyScale);
                rotation = (Quaternion.Inverse(root.rotation) * box.transform.rotation).eulerAngles;
            }
        }

        preview.SetVector3("Box_center", center);
        preview.SetVector3("Box_size", Vector3.Scale(size, smoke.transform.lossyScale));
        preview.SetVector3("Rotation", rotation);
        preview.SetVector3("Spread_center", spawn);
        preview.SetFloat("Spread_radius", 10000f);
        preview.SetFloat("FlameSpeed", 0.25f);
        preview.SetFloat("FireParticleMultiplier", engine.globalFireVFXMultiplier);
        preview.SetFloat("SmokeAlpha", Mathf.Max(0f, alpha) * previewStrength);
        preview.SetFloat("SmokeVFXMultiplier", template != null ? template.smokeVFXMultiplier : smoke.smokeVFXMultiplier);
        preview.SetFloat("SmokeParticleSize", template != null ? template.smokeParticleSize : smoke.smokeParticleSize);
        preview.SetVector4("SmokeColor", new Vector4(color.r * intensity, color.g * intensity, color.b * intensity, color.a));
        preview.SetFloat("CullingDist", engine.flameLodCullDistance);
        preview.SetFloat("LODMaxDist", engine.flameLodCullDistance * engine.flameLodStartToFadePercentage);
        Camera camera = SceneView.lastActiveSceneView?.camera;
        if (camera != null && preview.HasVector3("AdditionalCameraPosition"))
            preview.SetVector3("AdditionalCameraPosition", camera.transform.position);

        if (graphChanged)
            preview.Reinit();
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }

    private static VisualEffect GetSmokeEffect(FlameEngine engine)
    {
        GameObject prefab = engine == null ? null : engine.GetFireVFX(FlameEngine.FireVFXVariant.OnlySmoke);
        VisualEffect effect = prefab == null ? null : prefab.GetComponent<VisualEffect>();
        return effect != null && effect.visualEffectAsset != null ? effect : null;
    }

    // Estimate the two heights from the same local floor without building a runtime air map.
    private bool TryGetPositions(IgnisSmokeDiffusion smoke, out Vector3 spawn, out Vector3 layerBottom)
    {
        spawn = layerBottom = default;
        Transform anchor = previewAnchor != null ? previewAnchor : GetDefaultAnchor(smoke);
        if (anchor == null || anchor.gameObject.scene != smoke.gameObject.scene)
            return false;

        Vector3 floor = anchor.position;
        float nearest = float.PositiveInfinity;
        foreach (RaycastHit hit in Physics.RaycastAll(anchor.position + Vector3.up * 0.75f,
                     Vector3.down, 8f, smoke.obstacleMask, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.gameObject.scene != smoke.gameObject.scene ||
                hit.collider.name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) < 0 ||
                hit.distance >= nearest)
                continue;

            nearest = hit.distance;
            floor.y = hit.point.y;
        }

        spawn = floor + Vector3.up * smoke.heightOffset;
        layerBottom = floor + Vector3.up * smoke.lowestSmokeHeight;
        return true;
    }

    // Prefer current Inspector assignments over the automatically detected source.
    private Transform GetDefaultAnchor(IgnisSmokeDiffusion smoke)
    {
        foreach (var source in smoke.fireSources ?? System.Array.Empty<FlammableObject>())
        {
            if (source != null && source.gameObject.scene == smoke.gameObject.scene)
                return source.transform;
        }

        return smoke.TryGetComponent<FlammableObject>(out _) ? smoke.transform : defaultAnchor;
    }

    // Label the two authored heights so they can be judged against the room in Edit Mode.
    private void OnSceneGUI()
    {
        var smoke = (IgnisSmokeDiffusion)target;
        if (!showHeightMarkers || EditorApplication.isPlayingOrWillChangePlaymode ||
            !smoke.isActiveAndEnabled || !TryGetPositions(smoke, out Vector3 spawn, out Vector3 layerBottom))
            return;

        using (new Handles.DrawingScope(Color.cyan))
        {
            Handles.DrawWireDisc(spawn, Vector3.up, smoke.bodyRadius);
            Handles.Label(spawn, "Approximate smoke release");
        }

        using (new Handles.DrawingScope(Color.yellow))
        {
            Handles.DrawWireDisc(layerBottom, Vector3.up, smoke.bodyRadius + 0.15f);
            Handles.Label(layerBottom, "Lowest emitter center target");
        }
    }
}
