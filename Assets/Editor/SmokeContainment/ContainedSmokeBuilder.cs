// Builds the room-clipped copies of Ignis effects used by IgnisSmokeDiffusion.
// The clip itself is in SmokeRoomClip.hlsl; the search for the room a particle
// belongs to is in SmokeRoomSelect.hlsl and runs per particle, not per pixel.
//
// Smoke (Tools > Smoke Containment > Build Contained Smoke):
//   ContainedSmoke.shadergraph      draws an Ignis smoke particle the same way
//                                   the OnlySmoke output does, and hides the part
//                                   of it outside its room.
//   Only_smoke_contained.vfx        a copy of Only_smoke_neutral.vfx whose smoke
//                                   output uses that Shader Graph.
//   Only_smoke_contained.prefab     a copy of the OnlySmoke prefab that plays it.
//                                   IgnisSmokeDiffusion uses it when lingering
//                                   smoke is off, so every volume churns.
//   Only_smoke_plume.vfx            the same churning smoke, holding at most
//   Only_smoke_plume.prefab         PlumeCapacity particles instead of 1000. The
//                                   plume over the fire plays it. Up close every
//                                   plume particle covers most of the view, so
//                                   the count sets most of its cost. Its
//                                   particles are clipped to the room the plume
//                                   sits in, wherever they drift.
//   Only_smoke_lingering.vfx        the same clipped smoke, but each particle is
//   Only_smoke_lingering.prefab     released once and then lingers: it lives
//                                   as long as SmokeLifetime and churns slowly in
//                                   place where it was released, so a drifting
//                                   volume leaves smoke along its path.
//                                   SmokeSpawnRate starts and stops the release;
//                                   SmokeCeiling caps how high it goes. Each
//                                   particle is clipped to the room it was
//                                   released in, wherever it drifts.
//
// Flames (Tools > Smoke Containment > Build Contained Flames):
//   ContainedFlame.shadergraph      draws an additive flame or ember the same way
//                                   the Flame_box_wild outputs do, and hides the
//                                   part of it outside the burning object's room.
//   Flame_box_wild_contained.vfx    a copy of Flame_box_wild.vfx whose Flames and
//                                   Embers outputs use that Shader Graph. Its
//                                   Flames spawn FlameParticleShare of the usual
//                                   particles, FlameBrightness times as bright.
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
using UnityEditor.VFX.Block;
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

    const string PlumeVfxPath = OutputFolder + "/Only_smoke_plume.vfx";
    const string PlumePrefabPath = OutputFolder + "/Only_smoke_plume.prefab";
    // Ignis spawns far more smoke than the original 1000 slots hold, so the
    // plume always runs full: this is how many particles it shows.
    const uint PlumeCapacity = 250;

    const string LingeringVfxPath = OutputFolder + "/Only_smoke_lingering.vfx";
    const string LingeringPrefabPath = OutputFolder + "/Only_smoke_lingering.prefab";
    // Exposed properties IgnisSmokeDiffusion sets on each lingering volume.
    const string SpawnRateProperty = "SmokeSpawnRate";
    const string LifetimeProperty = "SmokeLifetime";
    // The highest a particle is released, in world meters: just under the ceiling above its volume.
    const string CeilingProperty = "SmokeCeiling";
    // FlammableObject sets Box to the volume's emission box every frame.
    const string SmokeBoxParameter = "Box";
    // A lingering volume holds a small, fixed batch of particles.
    const uint LingeringCapacity = 128;
    // Seconds a released particle takes to fade in and to grow to full size.
    const float LingerFadeInSeconds = 3f;
    const float LingerGrowSeconds = 12f;
    // The churning puff flipbook, slowed from about 25 to this many frames a second.
    const float LingerFramesPerSecond = 4f;
    // A particle lives for minutes, so any steady speed would carry it meters
    // from where it was released, out through the ceiling or floor where the room clip
    // hides it. Drag stops its launch speed, and a soft spring of this
    // strength (per second squared) pulls it back to where it was released.
    // Against the turbulence it churns within about 0.2 meters of that spot.
    const float LingerDrag = 0.6f;
    const float LingerTurbulence = 0.05f;
    const float LingerTether = 0.25f;
    // A churning particle brightens, then darkens over its short life. Weighted
    // by its alpha it shows at about this brightness, which a lingering particle holds.
    const float LingerBrightness = 0.95f;
    const float LingerAlpha = 0.47f;
    // A churning particle rose about 0.75 to 3 meters above its volume before
    // it died, which kept the smoke densest under the ceiling and left clearer
    // air below. A lingering particle is released up to this far above its
    // volume instead, but never above SmokeCeiling, so the highest ones gather
    // under the ceiling.
    const float LingerRise = 1.5f;

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
    // Up close the flames cover most of the view, and each fire runs 10,000 to
    // 15,000 of them. 60% of them with their color 1.25 times as bright cover the
    // same area with nearly the same orange edges and saturated core, and cost
    // about 40% less. Fewer, or brighter, turns the edges yellow.
    // IgnisSmokeDiffusion can override both.
    const string FlameShareProperty = "FlameParticleShare";
    const string FlameBrightnessProperty = "FlameBrightness";
    const float FlameShare = 0.6f;
    const float FlameBrightness = 1.25f;

    const string MainTextureName = "MainTexture";
    const string FlipbookSizeName = "FlipbookSize";
    const string TexIndexName = "TexIndex";
    const string ParticleColorName = "ParticleColor";
    const string ParticleAlphaName = "ParticleAlpha";
    // Index of the room box a particle belongs to. A Custom HLSL operator in
    // each VFX output works it out once per particle corner with
    // SmokeRoomSelect.hlsl, instead of the Shader Graph searching every room
    // box for every pixel.
    const string ParticleRoomName = "ParticleRoom";
    const string OwnerRoomName = "OwnerRoom";
    const string RoomSelectHlslPath = "Assets/Shaders/SmokeContainment/SmokeRoomSelect.hlsl";

    [MenuItem("Tools/Smoke Containment/Build Contained Smoke")]
    public static void BuildAll()
    {
        BuildShaderGraph();
        BuildVfx();
        BuildPrefab();
        BuildLingeringVfx();
        BuildLingeringPrefab();
        BuildPlumeVfx();
        BuildPlumePrefab();
        Debug.Log("[ContainedSmoke] Built " + ShaderGraphPath + ", " + VfxPath + ", " + PrefabPath + ", " + LingeringVfxPath + ", " + LingeringPrefabPath + ", " + PlumeVfxPath + " and " + PlumePrefabPath);
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
        WriteShaderGraph(ShaderGraphPath, "ContainedSmokeParticle", AlphaMode.Alpha, ParticleRoomName,
            SmokeTexture(), new Vector2(8f, 8f));
    }

    /// <summary>
    /// Feeds a smoke output's room input from the room search at a point of the
    /// particle: its center, or for lingering smoke the point it was released.
    /// </summary>
    static void LinkSmokeRoom(VFXGraph graph, VFXContext composed, Vector2 position, string attribute = "position")
    {
        LinkSlots(AttributeNode(graph, attribute, position).outputSlots[0],
            RoomSearch(graph, composed, ParticleRoomName, "SmokeParticleRoom", position));
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 2 Build VFX")]
    public static void BuildVfx()
    {
        var shaderGraph = LoadShaderGraph(ShaderGraphPath);
        var resource = CopyVfx(SourceVfxPath, OutputFolder, VfxPath);
        var graph = resource.GetOrCreateGraph();

        ConvertOutput(graph, SmokeSystemTitle, shaderGraph, ShaderGraphPath, SmokeTexture(), new Vector2(8f, 8f),
            (composed, position) => LinkSmokeRoom(graph, composed, position));

        WriteVfx(graph, resource, VfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 3 Build Prefab")]
    public static void BuildPrefab()
    {
        WritePrefab(SourcePrefabPath, OutputFolder, PrefabPath, VfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 4 Build Lingering VFX")]
    public static void BuildLingeringVfx()
    {
        var shaderGraph = LoadShaderGraph(ShaderGraphPath);
        var resource = CopyVfx(SourceVfxPath, OutputFolder, LingeringVfxPath);
        var graph = resource.GetOrCreateGraph();

        // A lingering particle belongs to the room it was released in. Its
        // release speed carries it up to half a meter before the tether pulls
        // it back, so near a wall its center can drift through it, and the room
        // at its center would draw it on the far side.
        ConvertOutput(graph, SmokeSystemTitle, shaderGraph, ShaderGraphPath, SmokeTexture(), new Vector2(8f, 8f),
            (composed, position) => LinkSmokeRoom(graph, composed, position, "targetPosition"));
        MakeSmokeLinger(graph);

        WriteVfx(graph, resource, LingeringVfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 5 Build Lingering Prefab")]
    public static void BuildLingeringPrefab()
    {
        WritePrefab(SourcePrefabPath, OutputFolder, LingeringPrefabPath, LingeringVfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 6 Build Plume VFX")]
    public static void BuildPlumeVfx()
    {
        var shaderGraph = LoadShaderGraph(ShaderGraphPath);
        var resource = CopyVfx(SourceVfxPath, OutputFolder, PlumeVfxPath);
        var graph = resource.GetOrCreateGraph();

        // The plume churns over the fire, which can stand next to a wall, and
        // its particles rise and drift for seconds, through that wall too. They
        // all belong to the room the plume sits in, the way flames belong to
        // the burning object's room, so none shows on the far side.
        ConvertOutput(graph, SmokeSystemTitle, shaderGraph, ShaderGraphPath, SmokeTexture(), new Vector2(8f, 8f),
            (composed, position) => LinkParameterField(graph, SmokeBoxParameter, "center",
                RoomSearch(graph, composed, ParticleRoomName, "SmokeParticleRoom", position)));
        SystemContext<VFXBasicInitialize>(graph, SmokeSystemTitle).GetData().SetSettingValue("capacity", PlumeCapacity);

        WriteVfx(graph, resource, PlumeVfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Smoke 7 Build Plume Prefab")]
    public static void BuildPlumePrefab()
    {
        WritePrefab(SourcePrefabPath, OutputFolder, PlumePrefabPath, PlumeVfxPath);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Flames 1 Build Shader Graph")]
    public static void BuildFlameShaderGraph()
    {
        var flames = SourceOutput(FlameSourceVfxPath, FlamesSystemTitle);
        WriteShaderGraph(FlameShaderGraphPath, "ContainedFlameParticle", AlphaMode.Additive, OwnerRoomName,
            OutputTexture(flames), OutputFlipbookSize(flames));
    }

    /// <summary>Feeds a flame output's room input from the room search at the burning object.</summary>
    static void LinkFlameRoom(VFXGraph graph, VFXContext composed, Vector2 position)
    {
        LinkParameterField(graph, FlameBoxParameter, "center",
            RoomSearch(graph, composed, OwnerRoomName, "FlameOwnerRoom", position));
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
                (composed, position) => LinkFlameRoom(graph, composed, position));
        }

        TrimFlames(graph);
        WriteVfx(graph, resource, FlameVfxPath);
    }

    /// <summary>
    /// Spawns FlameParticleShare of the Flames system's particles and makes
    /// each FlameBrightness times as bright, so the fire looks the same for
    /// less overdraw. Embers are left as they are.
    /// </summary>
    static void TrimFlames(VFXGraph graph)
    {
        var init = SystemContext<VFXBasicInitialize>(graph, FlamesSystemTitle);
        var output = SystemContext<VFXAbstractComposedParticleOutput>(graph, FlamesSystemTitle);
        int bursts = 0;
        foreach (var spawner in graph.children.OfType<VFXBasicSpawner>().Where(s => s.outputContexts.Contains(init)).ToList())
        {
            foreach (var burst in spawner.children.OfType<VFXSpawnerBurst>().ToList())
            {
                ScaleSlot(graph, Slot(burst, "Count"), FlameShareProperty, FlameShare, spawner.position + new Vector2(-420f, 240f));
                bursts++;
            }
        }
        if (bursts == 0)
            throw new InvalidOperationException("The Flames system has no burst spawner to scale.");

        ScaleSlot(graph, Slot(output, ParticleColorName), FlameBrightnessProperty, FlameBrightness, output.position + new Vector2(-420f, 360f));
    }

    /// <summary>
    /// Multiplies whatever feeds a slot by an exposed float. A slot fed one
    /// component at a time, like a burst's minimum and maximum count, has each
    /// component scaled.
    /// </summary>
    static void ScaleSlot(VFXGraph graph, VFXSlot target, string property, float defaultValue, Vector2 position)
    {
        var source = target.LinkedSlots.FirstOrDefault();
        if (source == null && target.children.Any(c => c.HasLink(true)))
        {
            int row = 0;
            foreach (var component in target.children.ToList())
                ScaleSlot(graph, component, property, defaultValue, position + new Vector2(0f, 80f * row++));
            return;
        }

        var scale = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.Multiply>();
        scale.position = position;
        graph.AddChild(scale);
        scale.SetOperandType(0, target.property.type);
        if (source != null)
        {
            target.UnlinkAll(true);
            LinkSlots(source, scale.inputSlots[0]);
        }
        else
        {
            scale.inputSlots[0].value = target.value;
        }

        LinkExposedFloat(graph, property, defaultValue, scale.inputSlots[1]);
        LinkSlots(scale.outputSlots[0], target);
    }

    [MenuItem("Tools/Smoke Containment/Steps/Flames 3 Build Prefab")]
    public static void BuildFlamePrefab()
    {
        WritePrefab(FlameSourcePrefabPath, FlameOutputFolder, FlamePrefabPath, FlameVfxPath);
    }

    /// <summary>
    /// An unlit, transparent, VFX-ready Shader Graph that calls the named
    /// function from SmokeRoomClip.hlsl. Its seventh input, roomName, is the
    /// index of the room box the particle belongs to, from RoomSearch; below
    /// zero means not clipped.
    /// </summary>
    static void WriteShaderGraph(string path, string functionName, AlphaMode alphaMode, string roomName,
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
        var particleRoom = AddProperty(graph, new Vector1ShaderProperty(), roomName);
        particleRoom.value = -1f;

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
        function.AddSlot(new Vector1MaterialSlot(6, "Room", "Room", SlotType.Input, -1f));
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
        Connect(graph, AddPropertyNode(graph, particleRoom, -400f, 250f), PropertyNode.OutputSlotId, function, 6);
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
    /// linkRoom feeds the room the particle belongs to, from RoomSearch.
    /// </summary>
    static void ConvertOutput(VFXGraph graph, string systemTitle, ShaderGraphVfxAsset shaderGraph, string shaderGraphPath,
        Texture texture, Vector2 flipbook, Action<VFXContext, Vector2> linkRoom)
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
        linkRoom(composed, outputPosition + new Vector2(-420f, 270f));
    }

    /// <summary>
    /// Turns the churning OnlySmoke system into one that releases a batch of
    /// particles and keeps them. The original bursts up to 20 particles every
    /// frame, each living 1.5 to 6 seconds while it rises and plays its
    /// flipbook at about 25 frames a second, so a volume is always full of new
    /// smoke. Here the script turns the release on only where its volume is
    /// passing through air that does not yet hold enough smoke. Each particle
    /// lives for SmokeLifetime and churns slowly in place where it was released,
    /// so a wandering volume leaves smoke behind it and the room fills evenly.
    /// </summary>
    static void MakeSmokeLinger(VFXGraph graph)
    {
        var init = SystemContext<VFXBasicInitialize>(graph, SmokeSystemTitle);
        var update = SystemContext<VFXBasicUpdate>(graph, SmokeSystemTitle);
        var output = SystemContext<VFXAbstractComposedParticleOutput>(graph, SmokeSystemTitle);
        var spawner = graph.children.OfType<VFXBasicSpawner>().First(s => s.outputContexts.Contains(init));

        init.GetData().SetSettingValue("capacity", LingeringCapacity);

        // Release: a rate the script controls, instead of a burst every frame.
        foreach (var burst in spawner.children.OfType<VFXSpawnerBurst>().ToList())
            RemoveBlock(spawner, burst);
        var rate = ScriptableObject.CreateInstance<VFXSpawnerConstantRate>();
        spawner.AddChild(rate);
        LinkExposedFloat(graph, SpawnRateProperty, 0f, rate.inputSlots[0]);

        // Lifetime: as long as the script asks, the same for every particle.
        var lifetime = init.children.OfType<SetAttribute>().First(b => b.attribute == "lifetime");
        foreach (var slot in lifetime.inputSlots)
            slot.UnlinkAll(true);
        lifetime.SetSettingValue("Random", RandomMode.Off);
        LinkExposedFloat(graph, LifetimeProperty, 600f, lifetime.inputSlots[0]);

        // Released somewhere between the volume and LingerRise above it.
        var placed = init.children.OfType<SetAttribute>().Last(b => b.attribute == "position");
        var rise = NewSetAttribute("position", RandomMode.PerComponent, AttributeCompositionMode.Add);
        init.AddChild(rise, init.GetIndex(placed) + 1);
        SetVector(rise.inputSlots[0], Vector3.zero);
        SetVector(rise.inputSlots[1], new Vector3(0f, LingerRise, 0f));

        // Never above the ceiling, where the room clip would hide it for good.
        var capped = NewSetAttribute("position", RandomMode.Off, AttributeCompositionMode.Overwrite);
        init.AddChild(capped, init.GetIndex(rise) + 1);
        LinkSlots(CapHeight(graph, init.position + new Vector2(-900f, 200f)), capped.inputSlots[0]);

        // Each particle starts on a random frame, so a batch does not churn in step.
        var frame = NewSetAttribute("texIndex", RandomMode.Uniform, AttributeCompositionMode.Overwrite);
        init.AddChild(frame);
        frame.inputSlots[0].value = 0f;
        frame.inputSlots[1].value = 63.99f;

        // Where it was released, in the world. It stays there while its volume
        // drifts on, so a wandering volume leaves smoke along its path and the
        // whole room fills instead of the smoke riding around in one clump.
        var anchor = NewSetAttribute("targetPosition", RandomMode.Off, AttributeCompositionMode.Overwrite);
        init.AddChild(anchor);
        LinkSlots(AttributeNode(graph, "position", init.position + new Vector2(-640f, 400f)).outputSlots[0], anchor.inputSlots[0]);

        // No steady updraft: the volume itself rises to its place in the layer.
        foreach (var force in update.children.OfType<Force>())
        {
            if (force.GetSetting("Mode").value.ToString() == "Relative")
                force.activationSlot.value = false;
        }

        int tetherIndex = 0;
        foreach (var turbulence in update.children.OfType<Turbulence>().ToList())
        {
            Slot(turbulence, "Intensity").value = LingerTurbulence;
            tetherIndex = update.GetIndex(turbulence) + 1;
        }

        // Pulled back toward where it was released, so it churns in place.
        var tether = ScriptableObject.CreateInstance<Force>();
        update.AddChild(tether, tetherIndex);
        tether.SetSettingValue("Mode", UnityEditor.VFX.Block.ForceMode.Absolute);
        LinkSlots(TetherForce(graph, update.position + new Vector2(-900f, 300f)), Slot(tether, "Force"));
        foreach (var drag in update.children.OfType<Drag>())
            Slot(drag, "dragCoefficient").value = LingerDrag;
        foreach (var flipbook in update.children.OfType<FlipbookPlay>())
        {
            var frameRate = Slot(flipbook, "frameRate");
            frameRate.UnlinkAll(true);
            frameRate.value = LingerFramesPerSecond;
        }

        // Size and color: grow and fade in over the first seconds, then hold.
        foreach (var curve in output.children.OfType<AttributeFromCurve>())
        {
            if (curve.attribute == "size")
            {
                curve.SetSettingValue("SampleMode", AttributeFromCurve.CurveSampleMode.Custom);
                LinkSlots(AgeRamp(graph, LingerGrowSeconds, output.position + new Vector2(-640f, 0f)), Slot(curve, "SampleTime"));
            }
            else if (curve.attribute == "color")
            {
                curve.SetSettingValue("SampleMode", AttributeFromCurve.CurveSampleMode.Custom);
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white * LingerBrightness, 1f) },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(LingerAlpha, 1f) });
                Slot(curve, "Color").value = gradient;
                LinkSlots(AgeRamp(graph, LingerFadeInSeconds, output.position + new Vector2(-640f, 120f)), Slot(curve, "SampleTime"));
            }
        }

        Debug.Log("[ContainedSmoke] Lingering smoke: capacity " + LingeringCapacity + ", release by " + SpawnRateProperty + ", life by " + LifetimeProperty);
    }

    static T SystemContext<T>(VFXGraph graph, string systemTitle) where T : VFXContext
    {
        var context = graph.children.OfType<T>().FirstOrDefault(c => c.GetData() != null && c.GetData().title == systemTitle);
        if (context == null)
            throw new InvalidOperationException("No " + typeof(T).Name + " on the '" + systemTitle + "' system.");
        return context;
    }

    static void RemoveBlock(VFXContext context, VFXBlock block)
    {
        foreach (var slot in block.inputSlots)
            slot.UnlinkAll(true);
        context.RemoveChild(block);
    }

    static SetAttribute NewSetAttribute(string attribute, RandomMode random, AttributeCompositionMode composition)
    {
        var block = ScriptableObject.CreateInstance<SetAttribute>();
        block.SetSettingValue("attribute", attribute);
        block.SetSettingValue("Random", random);
        block.SetSettingValue("Composition", composition);
        return block;
    }

    static VFXAttributeParameter AttributeNode(VFXGraph graph, string attribute, Vector2 position)
    {
        var node = ScriptableObject.CreateInstance<VFXAttributeParameter>();
        node.SetSettingValue("attribute", attribute);
        node.position = position;
        graph.AddChild(node);
        return node;
    }

    /// <summary>Particle age divided by seconds, clamped to 0..1.</summary>
    static VFXSlot AgeRamp(VFXGraph graph, float seconds, Vector2 position)
    {
        var divide = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.Divide>();
        divide.position = position + new Vector2(220f, 0f);
        graph.AddChild(divide);
        LinkSlots(AttributeNode(graph, "age", position).outputSlots[0], divide.inputSlots[0]);
        divide.inputSlots[1].value = Mathf.Max(0.01f, seconds);

        var saturate = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.Saturate>();
        saturate.position = position + new Vector2(400f, 0f);
        graph.AddChild(saturate);
        LinkSlots(divide.outputSlots[0], saturate.inputSlots[0]);
        return saturate.outputSlots[0];
    }

    /// <summary>The particle's position with its height capped at the CeilingProperty.</summary>
    static VFXSlot CapHeight(VFXGraph graph, Vector2 position)
    {
        var current = AttributeNode(graph, "position", position).outputSlots[0];

        var lowest = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.Minimum>();
        lowest.position = position + new Vector2(220f, 80f);
        graph.AddChild(lowest);
        LinkSlots(Component(current, "y"), lowest.inputSlots[0]);
        LinkExposedFloat(graph, CeilingProperty, 1000f, lowest.inputSlots[1]);

        var combined = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.AppendVector>();
        combined.position = position + new Vector2(440f, 0f);
        graph.AddChild(combined);
        combined.AddOperand();
        LinkSlots(Component(current, "x"), combined.inputSlots[0]);
        LinkSlots(lowest.outputSlots[0], combined.inputSlots[1]);
        LinkSlots(Component(current, "z"), combined.inputSlots[2]);
        return combined.outputSlots[0];
    }

    /// <summary>(targetPosition - position) * LingerTether: a spring back to the release point.</summary>
    static VFXSlot TetherForce(VFXGraph graph, Vector2 position)
    {
        var away = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.Subtract>();
        away.position = position + new Vector2(440f, 0f);
        graph.AddChild(away);
        away.SetOperandType(0, typeof(Vector3));
        away.SetOperandType(1, typeof(Vector3));
        LinkSlots(AttributeNode(graph, "targetPosition", position + new Vector2(220f, 0f)).outputSlots[0], away.inputSlots[0]);
        LinkSlots(AttributeNode(graph, "position", position + new Vector2(220f, 140f)).outputSlots[0], away.inputSlots[1]);

        var pull = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.Multiply>();
        pull.position = position + new Vector2(660f, 0f);
        graph.AddChild(pull);
        pull.SetOperandType(0, typeof(Vector3));
        LinkSlots(away.outputSlots[0], pull.inputSlots[0]);
        pull.inputSlots[1].value = LingerTether;
        return pull.outputSlots[0];
    }

    /// <summary>The x, y or z slot under a vector or position slot.</summary>
    static VFXSlot Component(VFXSlot vector, string name)
    {
        foreach (var child in vector.children)
        {
            if (child.name == name)
                return child;
            var nested = child.children.FirstOrDefault(c => c.name == name);
            if (nested != null)
                return nested;
        }

        throw new InvalidOperationException(vector.owner + "." + vector.name + " has no '" + name + "' component.");
    }

    static void LinkExposedFloat(VFXGraph graph, string name, float defaultValue, VFXSlot target)
    {
        var parameter = graph.children.OfType<VFXParameter>().FirstOrDefault(p => p.exposedName == name);
        if (parameter == null)
        {
            parameter = ScriptableObject.CreateInstance<VFXParameter>();
            parameter.Init(typeof(float));
            parameter.SetSettingValue("m_ExposedName", name);
            parameter.SetSettingValue("m_Exposed", true);
            parameter.value = defaultValue;
            graph.AddChild(parameter);
        }

        LinkSlots(parameter.outputSlots[0], target);
        parameter.ValidateNodes();
    }

    /// <summary>Position attributes take a Position slot, others a Vector3.</summary>
    static void SetVector(VFXSlot slot, Vector3 value)
    {
        if (slot.property.type == typeof(Position))
            slot.value = new Position { position = value };
        else
            slot.value = value;
    }

    static void LinkSlots(VFXSlot from, VFXSlot to)
    {
        if (!from.Link(to))
            throw new InvalidOperationException("Could not link " + from.owner + "." + from.name + " to " + to.owner + "." + to.name + ".");
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

    static VFXSlot Slot(IVFXSlotContainer container, string name)
    {
        var slot = container.inputSlots.FirstOrDefault(s => s.name == name);
        if (slot == null)
            throw new InvalidOperationException(container.GetType().Name + " has no '" + name + "' slot.");
        return slot;
    }

    /// <summary>
    /// Adds a Custom HLSL operator that runs selectFunction from
    /// SmokeRoomSelect.hlsl on a point and feeds the room box it returns to the
    /// output's slotName input. VFX evaluates Shader Graph inputs in the vertex
    /// stage, so the search over every room box runs once per particle corner
    /// rather than once per pixel. Returns the operator's point input to link.
    /// </summary>
    static VFXSlot RoomSearch(VFXGraph graph, VFXContext context, string slotName, string selectFunction, Vector2 position)
    {
        var search = ScriptableObject.CreateInstance<UnityEditor.VFX.Operator.CustomHLSL>();
        search.position = position + new Vector2(220f, 0f);
        graph.AddChild(search);
        search.SetSettingValue("m_OperatorName", "Room Search");
        search.SetSettingValue("m_HLSLCode",
            "#include \"" + RoomSelectHlslPath + "\"\n" +
            "float RoomOf(in float3 center)\n" +
            "{\n" +
            "    return " + selectFunction + "(center);\n" +
            "}\n");
        search.ResyncSlots(true);
        if (search.inputSlots.Count != 1 || search.outputSlots.Count != 1)
            throw new InvalidOperationException("The room search operator has " + search.inputSlots.Count + " inputs and " + search.outputSlots.Count + " outputs; expected 1 and 1.");

        LinkSlots(search.outputSlots[0], Slot(context, slotName));
        return search.inputSlots[0];
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
    static void LinkParameterField(VFXGraph graph, string exposedName, string field, VFXSlot target)
    {
        var parameter = graph.children
            .OfType<VFXParameter>()
            .FirstOrDefault(p => p.exposedName == exposedName);
        if (parameter == null)
            throw new InvalidOperationException("No exposed property '" + exposedName + "'.");

        var source = parameter.outputSlots[0].children.FirstOrDefault(s => s.name == field);
        if (source == null)
            throw new InvalidOperationException("Property '" + exposedName + "' has no '" + field + "'.");

        if (!source.Link(target))
            throw new InvalidOperationException("Could not link '" + exposedName + "." + field + "' to '" + target.owner + "." + target.name + "'.");

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
