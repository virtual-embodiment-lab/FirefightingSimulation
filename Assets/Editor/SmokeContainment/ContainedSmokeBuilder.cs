// Builds the room-clipped copies of Ignis effects used by IgnisSmokeDiffusion.
// The clip itself is in SmokeRoomClip.hlsl.
//
// Smoke (Tools > Smoke Containment > Build Contained Smoke):
//   ContainedSmoke.shadergraph      draws an Ignis smoke particle the same way
//                                   the OnlySmoke output does, and hides the part
//                                   of it outside its room.
//   Only_smoke_contained.vfx        a copy of Only_smoke_neutral.vfx whose smoke
//                                   output uses that Shader Graph.
//   Only_smoke_contained.prefab     a copy of the OnlySmoke prefab that plays it.
//
// Flames (Tools > Smoke Containment > Build Contained Flames):
//   ContainedFlame.shadergraph      draws an additive flame or ember the same way
//                                   the Flame_box_wild outputs do, and hides the
//                                   part of it outside the burning object's room.
//   Flame_box_wild_contained.vfx    a copy of Flame_box_wild.vfx whose Flames and
//                                   Embers outputs use that Shader Graph.
//   Flame_box_wild_contained.prefab a copy of the Wild prefab that plays it.
//
// The originals are untouched, so other scenes keep their smoke and fire.
//
// This assembly is named Unity.VisualEffectGraph.EditorTests because Shader
// Graph, VFX Graph and URP open their editor internals only to that name. The
// tool needs them to create the graph and rewire the VFX the way their own
// editors do. It is an editor-only tool, not a test assembly. Run it again after
// changing Only_smoke_neutral.vfx or Flame_box_wild.vfx.

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

    const string FlameShaderGraphPath = "Assets/Shaders/SmokeContainment/ContainedFlame.shadergraph";
    const string FlameSourceVfxPath = "Assets/OAVA-Flame/VFX/Flame_box_wild.vfx";
    const string FlameSourcePrefabPath = "Assets/OAVA-Flame/Prefabs/Flame_box_wild.prefab";
    const string FlameOutputFolder = "Assets/Resources/Fire";
    const string FlameVfxPath = FlameOutputFolder + "/Flame_box_wild_contained.vfx";
    const string FlamePrefabPath = FlameOutputFolder + "/Flame_box_wild_contained.prefab";
    const string FlamesSystemTitle = "Flames";
    const string EmbersSystemTitle = "Embers";
    // FlammableObject sets Box to the emission box of the burning object.
    const string FlameBoxParameter = "Box";

    const string MainTextureName = "MainTexture";
    const string FlipbookSizeName = "FlipbookSize";
    const string TexIndexName = "TexIndex";
    const string ParticleColorName = "ParticleColor";
    const string ParticleAlphaName = "ParticleAlpha";
    const string ParticleCenterName = "ParticleCenter";
    const string OwnerName = "Owner";

    [MenuItem("Tools/Smoke Containment/Build Contained Smoke")]
    public static void BuildAll()
    {
        BuildShaderGraph();
        BuildVfx();
        BuildPrefab();
        Debug.Log("[ContainedSmoke] Built " + ShaderGraphPath + ", " + VfxPath + " and " + PrefabPath);
    }

    [MenuItem("Tools/Smoke Containment/Build Contained Flames")]
    public static void BuildAllFlames()
    {
        BuildFlameShaderGraph();
        BuildFlameVfx();
        BuildFlamePrefab();
        Debug.Log("[ContainedSmoke] Built " + FlameShaderGraphPath + ", " + FlameVfxPath + " and " + FlamePrefabPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 1 Build Shader Graph")]
    public static void BuildShaderGraph()
    {
        WriteShaderGraph(ShaderGraphPath, "ContainedSmokeParticle", AlphaMode.Alpha, ParticleCenterName,
            SmokeTexture(), new Vector2(8f, 8f));
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 2 Build VFX")]
    public static void BuildVfx()
    {
        var shaderGraph = LoadShaderGraph(ShaderGraphPath);
        var resource = CopyVfx(SourceVfxPath, OutputFolder, VfxPath);
        var graph = resource.GetOrCreateGraph();

        ConvertOutput(graph, SmokeSystemTitle, shaderGraph, ShaderGraphPath, SmokeTexture(), new Vector2(8f, 8f),
            (composed, position) => LinkAttribute(graph, composed, "position", ParticleCenterName, position));

        WriteVfx(graph, resource, VfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 3 Build Prefab")]
    public static void BuildPrefab()
    {
        WritePrefab(SourcePrefabPath, OutputFolder, PrefabPath, VfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Flames 1 Build Shader Graph")]
    public static void BuildFlameShaderGraph()
    {
        var flames = SourceOutput(FlameSourceVfxPath, FlamesSystemTitle);
        WriteShaderGraph(FlameShaderGraphPath, "ContainedFlameParticle", AlphaMode.Additive, OwnerName,
            OutputTexture(flames), OutputFlipbookSize(flames));
    }

    [MenuItem("Tools/Smoke Containment/Steps/Flames 2 Build VFX")]
    public static void BuildFlameVfx()
    {
        var shaderGraph = LoadShaderGraph(FlameShaderGraphPath);
        var resource = CopyVfx(FlameSourceVfxPath, FlameOutputFolder, FlameVfxPath);
        var graph = resource.GetOrCreateGraph();

        foreach (string system in new[] { FlamesSystemTitle, EmbersSystemTitle })
        {
            var source = SystemOutput(graph, system);
            ConvertOutput(graph, system, shaderGraph, FlameShaderGraphPath, OutputTexture(source), OutputFlipbookSize(source),
                (composed, position) => LinkParameter(graph, composed, FlameBoxParameter, "center", OwnerName));
        }

        WriteVfx(graph, resource, FlameVfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Flames 3 Build Prefab")]
    public static void BuildFlamePrefab()
    {
        WritePrefab(FlameSourcePrefabPath, FlameOutputFolder, FlamePrefabPath, FlameVfxPath);
    }

    /// <summary>
    /// An unlit, transparent, VFX-ready Shader Graph that calls the named
    /// function from SmokeRoomClip.hlsl. Its seventh input, centerName, is the
    /// point that decides which room a particle belongs to.
    /// </summary>
    static void WriteShaderGraph(string path, string functionName, AlphaMode alphaMode, string centerName,
        Texture texture, Vector2 flipbook)
    {
        var target = (UniversalTarget)Activator.CreateInstance(typeof(UniversalTarget));
        target.TrySetActiveSubTarget(typeof(UniversalUnlitSubTarget));
        target.surfaceType = SurfaceType.Transparent;
        target.alphaMode = alphaMode;
        target.renderFace = RenderFace.Both;
        // Shader Graph defaults to casting shadows. The original Ignis smoke and
        // flames do not, and their shadows darken the house and street outside.
        target.castShadows = false;
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
        mainTexture.value = new SerializableTexture { texture = texture };
        var flipbookSize = AddProperty(graph, new Vector2ShaderProperty(), FlipbookSizeName);
        flipbookSize.value = new Vector4(flipbook.x, flipbook.y, 0f, 0f);
        var texIndex = AddProperty(graph, new Vector1ShaderProperty(), TexIndexName);
        var particleColor = AddProperty(graph, new Vector3ShaderProperty(), ParticleColorName);
        particleColor.value = new Vector4(1f, 1f, 1f, 0f);
        var particleAlpha = AddProperty(graph, new Vector1ShaderProperty(), ParticleAlphaName);
        particleAlpha.value = 1f;
        var particleCenter = AddProperty(graph, new Vector3ShaderProperty(), centerName);

        var function = new CustomFunctionNode();
        function.sourceType = HlslSourceType.File;
        function.functionName = functionName;
        function.functionSource = AssetDatabase.AssetPathToGUID(HlslPath);
        function.AddSlot(new Texture2DInputMaterialSlot(0, MainTextureName, MainTextureName));
        function.AddSlot(new Vector2MaterialSlot(1, "UV", "UV", SlotType.Input, Vector2.zero));
        function.AddSlot(new Vector2MaterialSlot(2, FlipbookSizeName, FlipbookSizeName, SlotType.Input, Vector2.one));
        function.AddSlot(new Vector1MaterialSlot(3, TexIndexName, TexIndexName, SlotType.Input, 0f));
        function.AddSlot(new Vector3MaterialSlot(4, ParticleColorName, ParticleColorName, SlotType.Input, Vector3.one));
        function.AddSlot(new Vector1MaterialSlot(5, ParticleAlphaName, ParticleAlphaName, SlotType.Input, 1f));
        function.AddSlot(new Vector3MaterialSlot(6, centerName, centerName, SlotType.Input, Vector3.zero));
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

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        FileUtilities.WriteShaderGraphToDisk(path, graph);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        Debug.Log("[ContainedSmoke] Wrote " + path);
    }

    static ShaderGraphVfxAsset LoadShaderGraph(string path)
    {
        var shaderGraph = AssetDatabase.LoadAssetAtPath<ShaderGraphVfxAsset>(path);
        if (shaderGraph == null)
            throw new InvalidOperationException("Build the Shader Graph first: " + path + " has no VFX sub-asset.");
        return shaderGraph;
    }

    static VisualEffectResource CopyVfx(string sourcePath, string folder, string path)
    {
        EnsureFolder(folder);
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null)
            AssetDatabase.DeleteAsset(path);
        if (!AssetDatabase.CopyAsset(sourcePath, path))
            throw new InvalidOperationException("Could not copy " + sourcePath);

        return VisualEffectResource.GetResourceAtPath(path);
    }

    static void WriteVfx(VFXGraph graph, VisualEffectResource resource, string path)
    {
        graph.SetExpressionGraphDirty();
        graph.RecompileIfNeeded();
        resource.WriteAssetWithSubAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        Debug.Log("[ContainedSmoke] Wrote " + path);
    }

    /// <summary>
    /// Switches one system's output to the Shader Graph and feeds it the
    /// particle's flipbook frame, color and alpha, like the output it replaces.
    /// linkCenter connects the point that picks the particle's room.
    /// </summary>
    static void ConvertOutput(VFXGraph graph, string systemTitle, ShaderGraphVfxAsset shaderGraph, string shaderGraphPath,
        Texture texture, Vector2 flipbook, Action<VFXContext, Vector2> linkCenter)
    {
        var output = SystemOutput(graph, systemTitle);
        var data = output.GetData();
        Vector2 outputPosition = output.position;
        int sortingPriority = output.GetMaterialSortingPriority();
        bool hasFlipbook = output.inputSlots.Any(s => s.name == "flipBookSize");

        output.SetSettingValue("shaderGraph", shaderGraph);
        SanitizeHelper.MigrateSGOutputToComposed(output);

        var composed = graph.children
            .OfType<VFXAbstractComposedParticleOutput>()
            .FirstOrDefault(o => o.GetData() == data);
        if (composed == null)
            throw new InvalidOperationException("The " + systemTitle + " output did not convert to a Shader Graph output.");

        KeepSortingPriority(composed, shaderGraphPath, sortingPriority);
        composed.ResyncSlots(true);
        Debug.Log("[ContainedSmoke] " + systemTitle + " Shader Graph output slots: " + string.Join(", ", composed.inputSlots.Select(s => s.name + ":" + s.property.type.Name)));

        Slot(composed, MainTextureName).value = texture;
        Slot(composed, FlipbookSizeName).value = flipbook;

        // An output without a flipbook draws the whole texture: frame 0 of a 1x1 sheet.
        if (hasFlipbook)
            LinkAttribute(graph, composed, "texIndex", TexIndexName, outputPosition + new Vector2(-420f, 0f));
        else
            Slot(composed, TexIndexName).value = 0f;

        LinkAttribute(graph, composed, "color", ParticleColorName, outputPosition + new Vector2(-420f, 90f));
        LinkAttribute(graph, composed, "alpha", ParticleAlphaName, outputPosition + new Vector2(-420f, 180f));
        linkCenter(composed, outputPosition + new Vector2(-420f, 270f));
    }

    static void WritePrefab(string sourcePrefabPath, string folder, string prefabPath, string vfxPath)
    {
        var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(vfxPath);
        if (asset == null)
            throw new InvalidOperationException("Build the VFX first: " + vfxPath);

        EnsureFolder(folder);
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
            AssetDatabase.DeleteAsset(prefabPath);
        if (!AssetDatabase.CopyAsset(sourcePrefabPath, prefabPath))
            throw new InvalidOperationException("Could not copy " + sourcePrefabPath);

        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            root.name = Path.GetFileNameWithoutExtension(prefabPath);
            var effect = root.GetComponent<VisualEffect>();
            var renderer = root.GetComponent<VFXRenderer>();
            // Assigning the asset copies its renderer settings onto the prefab.
            // Keep the source prefab's no-shadow setup.
            var castShadows = renderer.shadowCastingMode;
            effect.visualEffectAsset = asset;
            renderer.shadowCastingMode = castShadows;
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ContainedSmoke] Wrote " + prefabPath);
    }

    static VFXShaderGraphParticleOutput SystemOutput(VFXGraph graph, string systemTitle)
    {
        var output = graph.children
            .OfType<VFXShaderGraphParticleOutput>()
            .FirstOrDefault(o => o.GetData() != null && o.GetData().title == systemTitle);
        if (output == null)
            throw new InvalidOperationException("No output on the '" + systemTitle + "' system.");
        return output;
    }

    static VFXAbstractParticleOutput SourceOutput(string vfxPath, string systemTitle)
    {
        var source = VisualEffectResource.GetResourceAtPath(vfxPath).GetOrCreateGraph();
        return source.children
            .OfType<VFXAbstractParticleOutput>()
            .First(o => o.GetData() != null && o.GetData().title == systemTitle);
    }

    static Texture OutputTexture(VFXContext output)
    {
        return (Texture)output.inputSlots.First(s => s.name == "mainTexture").value;
    }

    static Vector2 OutputFlipbookSize(VFXContext output)
    {
        var slot = output.inputSlots.FirstOrDefault(s => s.name == "flipBookSize");
        return slot != null ? (Vector2)(FlipBook)slot.value : Vector2.one;
    }

    static Texture2D SmokeTexture()
    {
        return (Texture2D)OutputTexture(SourceOutput(SourceVfxPath, SmokeSystemTitle));
    }

    /// <summary>
    /// The original outputs set their sorting priority (smoke draws after the
    /// fire at 10, so it darkens the flames behind it). A Shader Graph output
    /// reads its priority from the material's queue offset instead, which the
    /// conversion leaves at 0, so it is set here.
    /// </summary>
    static void KeepSortingPriority(VFXAbstractComposedParticleOutput composed, string shaderGraphPath, int sortingPriority)
    {
        object shading = composed.GetSetting("m_Shading").value;
        var settings = (VFXMaterialSerializedSettings)shading.GetType()
            .GetField("materialSettings", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(shading);

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderGraphPath);
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
        Debug.Log("[ContainedSmoke] Output sorting priority: " + composed.GetMaterialSortingPriority());
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

    /// <summary>
    /// Links a field of an exposed property, such as Box.center, to a slot. The
    /// fire effects are spawned at the world origin with no rotation, so their
    /// local space is world space.
    /// </summary>
    static void LinkParameter(VFXGraph graph, VFXContext context, string exposedName, string field, string slotName)
    {
        var parameter = graph.children
            .OfType<VFXParameter>()
            .FirstOrDefault(p => p.exposedName == exposedName);
        if (parameter == null)
            throw new InvalidOperationException("No exposed property '" + exposedName + "'.");

        var source = parameter.outputSlots[0].children.FirstOrDefault(s => s.name == field);
        if (source == null)
            throw new InvalidOperationException("Property '" + exposedName + "' has no '" + field + "'.");

        if (!source.Link(Slot(context, slotName)))
            throw new InvalidOperationException("Could not link '" + exposedName + "." + field + "' to '" + slotName + "'.");

        // Attaches the new link to one of the property's nodes in the graph view.
        parameter.ValidateNodes();
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
