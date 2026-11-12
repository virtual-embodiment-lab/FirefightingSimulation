// Builds the contained smoke assets used by IgnisSmokeDiffusion:
//   ContainedSmoke.shadergraph  draws an Ignis smoke particle the same way the
//                               OnlySmoke output does, and hides the part of it
//                               outside its room (SmokeRoomClip.hlsl).
//   Only_smoke_contained.vfx    a copy of Only_smoke_neutral.vfx whose smoke
//                               output uses that Shader Graph.
//   Only_smoke_contained.prefab a copy of the OnlySmoke prefab that plays it.
// The originals are untouched, so other scenes keep their smoke.
//
// This assembly is named Unity.VisualEffectGraph.EditorTests because Shader
// Graph, VFX Graph and URP open their editor internals only to that name. The
// tool needs them to create the graph and rewire the VFX the way their own
// editors do. It is an editor-only tool, not a test assembly. Run it again from
// Tools > Smoke Containment after changing Only_smoke_neutral.vfx.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;

static class ContainedSmokeBuilder
{
    const string HlslPath = "Assets/Shaders/SmokeContainment/SmokeRoomClip.hlsl";
    const string ShaderGraphPath = "Assets/Shaders/SmokeContainment/ContainedSmoke.shadergraph";
    const string SourceVfxPath = "Assets/OAVA-Flame/VFX/Only_smoke_neutral.vfx";
    const string SourcePrefabPath = "Assets/OAVA-Flame/Prefabs/Only_smoke_neutral.prefab";
    const string OutputFolder = "Assets/Resources/Smoke";
    const string VfxPath = OutputFolder + "/Only_smoke_contained.vfx";
    const string PrefabPath = OutputFolder + "/Only_smoke_contained.prefab";
    const string SmokeSystemTitle = "Smoke";

    const string MainTextureName = "MainTexture";
    const string FlipbookSizeName = "FlipbookSize";
    const string TexIndexName = "TexIndex";
    const string ParticleColorName = "ParticleColor";
    const string ParticleAlphaName = "ParticleAlpha";
    const string ParticleCenterName = "ParticleCenter";

    [MenuItem("Tools/Smoke Containment/Build Contained Smoke")]
    public static void BuildAll()
    {
        BuildShaderGraph();
        BuildVfx();
        BuildPrefab();
        Debug.Log("[ContainedSmoke] Built " + ShaderGraphPath + ", " + VfxPath + " and " + PrefabPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/1 Build Shader Graph")]
    public static void BuildShaderGraph()
    {
        var target = (UniversalTarget)Activator.CreateInstance(typeof(UniversalTarget));
        target.TrySetActiveSubTarget(typeof(UniversalUnlitSubTarget));
        target.surfaceType = SurfaceType.Transparent;
        target.alphaMode = AlphaMode.Alpha;
        target.renderFace = RenderFace.Both;
        // The Support VFX Graph toggle has no public setter.
        typeof(UniversalTarget)
            .GetField("m_SupportVFX", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(target, true);

        var blocks = new[]
        {
            BlockFields.VertexDescription.Position,
            BlockFields.VertexDescription.Normal,
            BlockFields.VertexDescription.Tangent,
            BlockFields.SurfaceDescription.BaseColor,
            BlockFields.SurfaceDescription.Alpha,
        };

        var graph = new GraphData();
        graph.AddContexts();
        graph.InitializeOutputs(new Target[] { target }, blocks);
        graph.AddCategory(CategoryData.DefaultCategory());
        graph.path = "Shader Graphs";

        var mainTexture = AddProperty(graph, new Texture2DShaderProperty(), MainTextureName);
        mainTexture.value = new SerializableTexture { texture = SmokeTexture() };
        var flipbookSize = AddProperty(graph, new Vector2ShaderProperty(), FlipbookSizeName);
        flipbookSize.value = new Vector4(8f, 8f, 0f, 0f);
        var texIndex = AddProperty(graph, new Vector1ShaderProperty(), TexIndexName);
        var particleColor = AddProperty(graph, new Vector3ShaderProperty(), ParticleColorName);
        particleColor.value = new Vector4(1f, 1f, 1f, 0f);
        var particleAlpha = AddProperty(graph, new Vector1ShaderProperty(), ParticleAlphaName);
        particleAlpha.value = 1f;
        var particleCenter = AddProperty(graph, new Vector3ShaderProperty(), ParticleCenterName);

        var function = new CustomFunctionNode();
        function.sourceType = HlslSourceType.File;
        function.functionName = "ContainedSmokeParticle";
        function.functionSource = AssetDatabase.AssetPathToGUID(HlslPath);
        function.AddSlot(new Texture2DInputMaterialSlot(0, MainTextureName, MainTextureName));
        function.AddSlot(new Vector2MaterialSlot(1, "UV", "UV", SlotType.Input, Vector2.zero));
        function.AddSlot(new Vector2MaterialSlot(2, FlipbookSizeName, FlipbookSizeName, SlotType.Input, Vector2.one));
        function.AddSlot(new Vector1MaterialSlot(3, TexIndexName, TexIndexName, SlotType.Input, 0f));
        function.AddSlot(new Vector3MaterialSlot(4, ParticleColorName, ParticleColorName, SlotType.Input, Vector3.one));
        function.AddSlot(new Vector1MaterialSlot(5, ParticleAlphaName, ParticleAlphaName, SlotType.Input, 1f));
        function.AddSlot(new Vector3MaterialSlot(6, ParticleCenterName, ParticleCenterName, SlotType.Input, Vector3.zero));
        function.AddSlot(new Vector3MaterialSlot(7, "WorldPosition", "WorldPosition", SlotType.Input, Vector3.zero));
        function.AddSlot(new Vector3MaterialSlot(8, "Color", "Color", SlotType.Output, Vector3.zero));
        function.AddSlot(new Vector1MaterialSlot(9, "Alpha", "Alpha", SlotType.Output, 0f));
        Place(function, 0f, 0f);
        graph.AddNode(function);

        var uv = new UVNode();
        Place(uv, -400f, -160f);
        graph.AddNode(uv);

        // Defaults to World space, which in URP is absolute world space.
        var position = new PositionNode();
        Place(position, -400f, 320f);
        graph.AddNode(position);

        Connect(graph, AddPropertyNode(graph, mainTexture, -400f, -260f), PropertyNode.OutputSlotId, function, 0);
        Connect(graph, uv, UVNode.OutputSlotId, function, 1);
        Connect(graph, AddPropertyNode(graph, flipbookSize, -400f, -60f), PropertyNode.OutputSlotId, function, 2);
        Connect(graph, AddPropertyNode(graph, texIndex, -400f, 20f), PropertyNode.OutputSlotId, function, 3);
        Connect(graph, AddPropertyNode(graph, particleColor, -400f, 100f), PropertyNode.OutputSlotId, function, 4);
        Connect(graph, AddPropertyNode(graph, particleAlpha, -400f, 180f), PropertyNode.OutputSlotId, function, 5);
        Connect(graph, AddPropertyNode(graph, particleCenter, -400f, 250f), PropertyNode.OutputSlotId, function, 6);
        Connect(graph, position, 0, function, 7);

        Connect(graph, function, 8, Block(graph, BlockFields.SurfaceDescription.BaseColor), 0);
        Connect(graph, function, 9, Block(graph, BlockFields.SurfaceDescription.Alpha), 0);

        Directory.CreateDirectory(Path.GetDirectoryName(ShaderGraphPath));
        FileUtilities.WriteShaderGraphToDisk(ShaderGraphPath, graph);
        AssetDatabase.ImportAsset(ShaderGraphPath, ImportAssetOptions.ForceUpdate);
        Debug.Log("[ContainedSmoke] Wrote " + ShaderGraphPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/2 Build VFX")]
    public static void BuildVfx()
    {
        var shaderGraph = AssetDatabase.LoadAssetAtPath<ShaderGraphVfxAsset>(ShaderGraphPath);
        if (shaderGraph == null)
            throw new InvalidOperationException("Build the Shader Graph first: " + ShaderGraphPath + " has no VFX sub-asset.");

        EnsureFolder(OutputFolder);
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(VfxPath) != null)
            AssetDatabase.DeleteAsset(VfxPath);
        if (!AssetDatabase.CopyAsset(SourceVfxPath, VfxPath))
            throw new InvalidOperationException("Could not copy " + SourceVfxPath);

        var resource = VisualEffectResource.GetResourceAtPath(VfxPath);
        var graph = resource.GetOrCreateGraph();

        var output = graph.children
            .OfType<VFXShaderGraphParticleOutput>()
            .FirstOrDefault(o => o.GetData() != null && o.GetData().title == SmokeSystemTitle);
        if (output == null)
            throw new InvalidOperationException("No output on the '" + SmokeSystemTitle + "' system in " + VfxPath);

        var data = output.GetData();
        Vector2 outputPosition = output.position;
        int sortingPriority = output.GetMaterialSortingPriority();

        output.SetSettingValue("shaderGraph", shaderGraph);
        SanitizeHelper.MigrateSGOutputToComposed(output);

        var composed = graph.children
            .OfType<VFXAbstractComposedParticleOutput>()
            .FirstOrDefault(o => o.GetData() == data);
        if (composed == null)
            throw new InvalidOperationException("The smoke output did not convert to a Shader Graph output.");

        KeepSortingPriority(composed, sortingPriority);
        composed.ResyncSlots(true);
        Debug.Log("[ContainedSmoke] Shader Graph output slots: " + string.Join(", ", composed.inputSlots.Select(s => s.name + ":" + s.property.type.Name)));

        Slot(composed, MainTextureName).value = SmokeTexture();
        Slot(composed, FlipbookSizeName).value = new Vector2(8f, 8f);

        LinkAttribute(graph, composed, "texIndex", TexIndexName, outputPosition + new Vector2(-420f, 0f));
        LinkAttribute(graph, composed, "color", ParticleColorName, outputPosition + new Vector2(-420f, 90f));
        LinkAttribute(graph, composed, "alpha", ParticleAlphaName, outputPosition + new Vector2(-420f, 180f));
        LinkAttribute(graph, composed, "position", ParticleCenterName, outputPosition + new Vector2(-420f, 270f));

        graph.SetExpressionGraphDirty();
        graph.RecompileIfNeeded();
        resource.WriteAssetWithSubAssets();
        AssetDatabase.ImportAsset(VfxPath, ImportAssetOptions.ForceUpdate);
        Debug.Log("[ContainedSmoke] Wrote " + VfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/3 Build Prefab")]
    public static void BuildPrefab()
    {
        var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(VfxPath);
        if (asset == null)
            throw new InvalidOperationException("Build the VFX first: " + VfxPath);

        EnsureFolder(OutputFolder);
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            AssetDatabase.DeleteAsset(PrefabPath);
        if (!AssetDatabase.CopyAsset(SourcePrefabPath, PrefabPath))
            throw new InvalidOperationException("Could not copy " + SourcePrefabPath);

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            root.name = Path.GetFileNameWithoutExtension(PrefabPath);
            var effect = root.GetComponent<VisualEffect>();
            effect.visualEffectAsset = asset;
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ContainedSmoke] Wrote " + PrefabPath);
    }

    static Texture2D SmokeTexture()
    {
        var source = VisualEffectResource.GetResourceAtPath(SourceVfxPath).GetOrCreateGraph();
        var output = source.children
            .OfType<VFXAbstractParticleOutput>()
            .First(o => o.GetData() != null && o.GetData().title == SmokeSystemTitle);
        return (Texture2D)output.inputSlots.First(s => s.name == "mainTexture").value;
    }

    /// <summary>
    /// The original smoke output draws after the fire (sorting priority 10), so
    /// the smoke darkens the flames behind it. A Shader Graph output reads its
    /// priority from the material's queue offset instead, which the conversion
    /// leaves at 0, so it is set here.
    /// </summary>
    static void KeepSortingPriority(VFXAbstractComposedParticleOutput composed, int sortingPriority)
    {
        object shading = composed.GetSetting("m_Shading").value;
        var settings = (VFXMaterialSerializedSettings)shading.GetType()
            .GetField("materialSettings", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(shading);

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderGraphPath);
        var material = new Material(shader);
        try
        {
            material.SetFloat("_QueueOffset", sortingPriority);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + sortingPriority;
            settings.SyncFromMaterial(material);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(material);
        }

        composed.SetSettingValue("sortingPriority", sortingPriority);
        composed.Invalidate(VFXModel.InvalidationCause.kSettingChanged);
        Debug.Log("[ContainedSmoke] Smoke output sorting priority: " + composed.GetMaterialSortingPriority());
    }

    static T AddProperty<T>(GraphData graph, T property, string name) where T : AbstractShaderProperty
    {
        property.displayName = name;
        graph.AddGraphInput(property);
        property.SetReferenceNameAndSanitizeForGraph(graph, name);
        return property;
    }

    static PropertyNode AddPropertyNode(GraphData graph, AbstractShaderProperty property, float x, float y)
    {
        var node = new PropertyNode();
        Place(node, x, y);
        graph.AddNode(node);
        node.property = property;
        return node;
    }

    static void Place(AbstractMaterialNode node, float x, float y)
    {
        var state = node.drawState;
        state.position = new Rect(x, y, 0f, 0f);
        node.drawState = state;
    }

    static BlockNode Block(GraphData graph, BlockFieldDescriptor descriptor)
    {
        return graph.GetNodes<BlockNode>().First(b => b.descriptor == descriptor);
    }

    static void Connect(GraphData graph, AbstractMaterialNode from, int fromSlot, AbstractMaterialNode to, int toSlot)
    {
        if (graph.Connect(from.GetSlotReference(fromSlot), to.GetSlotReference(toSlot)) == null)
            throw new InvalidOperationException("Could not connect " + from.name + " slot " + fromSlot + " to " + to.name + " slot " + toSlot);
    }

    static VFXSlot Slot(VFXContext context, string name)
    {
        var slot = context.inputSlots.FirstOrDefault(s => s.name == name);
        if (slot == null)
            throw new InvalidOperationException("The Shader Graph output has no '" + name + "' slot.");
        return slot;
    }

    static void LinkAttribute(VFXGraph graph, VFXContext context, string attribute, string slotName, Vector2 position)
    {
        var getAttribute = ScriptableObject.CreateInstance<VFXAttributeParameter>();
        getAttribute.SetSettingValue("attribute", attribute);
        getAttribute.position = position;
        graph.AddChild(getAttribute);
        if (!getAttribute.outputSlots[0].Link(Slot(context, slotName)))
            throw new InvalidOperationException("Could not link attribute '" + attribute + "' to '" + slotName + "'.");
    }

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;

        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
