using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ignis;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

// Unity 6's VFX Graph authoring model is internal. Keep reflection in this editor command.
public static class IgnisSmokeContainmentGraph
{
    private const string Original = "Assets/OAVA-Flame/VFX/Only_smoke_neutral.vfx";
    private const string Variant = "Assets/VFX/IgnisSmokeContained.vfx";
    private const string EnginePrefab = "Assets/OAVA-Flame/Prefabs/FlameEngine.prefab";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [MenuItem("Tools/Smoke Geometry/Build Ignis Containment Graph")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before building the smoke graph.");

        // Rebuild the generated variant from the original, preserving its asset GUID.
        if (File.Exists(Variant))
            File.Copy(Original, Variant, true);
        else if (!AssetDatabase.CopyAsset(Original, Variant))
            throw new InvalidOperationException("Could not copy the Ignis OnlySmoke graph.");
        AssetDatabase.ImportAsset(Variant, ImportAssetOptions.ForceSynchronousImport);

        const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        object resource = FindType("VisualEffectResource").GetMethod("GetResourceAtPath", staticFlags)
            .Invoke(null, new object[] { Variant });
        object graph = FindType("VisualEffectResourceExtensions").GetMethod("GetOrCreateGraph", staticFlags)
            .Invoke(null, new[] { resource });
        var nodes = Children(graph, "children");
        // These are the smoke contexts. The graph's other particle system handles steam.
        object initialize = FindNode(nodes, 8926484042661614914L);
        object update = FindNode(nodes, 8926484042661614865L);
        object output = FindNode(nodes, 8926484042661614968L);
        object box = FindNode(nodes, 8926484042661615063L);
        object boxCenter = Children(Children(box, "outputSlots")[0], "children")
            .Single(slot => SlotName(slot) == "center");

        object integration = Invoke(update, "GetSettingValue", "integration");
        Invoke(update, "SetSettingValue", "integration", Enum.Parse(integration.GetType(), "None"));
        object deltaTime = Create("VFXDynamicBuiltInParameter");
        Type flags = deltaTime.GetType().GetNestedType("BuiltInFlag");
        Invoke(deltaTime, "SetSettingValue", "m_BuiltInParameters", Enum.Parse(flags, "VfxDeltaTime"));
        Invoke(graph, "AddChild", deltaTime);

        var parameters = new Dictionary<string, object>();
        AddBlock(initialize, "Birth", "Reject births across solid surfaces", "EmitterCenter", boxCenter);
        AddBlock(update, "Motion", "Move and slide against solid surfaces", "StepSeconds", Children(deltaTime, "outputSlots")[0]);
        AddBlock(output, "Fade", "Fade smoke sprites near walls");
        Invoke(graph, "UpdateSubAssets");
        Invoke(resource, "WriteAsset");
        AssetDatabase.ImportAsset(Variant, ImportAssetOptions.ForceSynchronousImport);

        var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(Variant);
        if (asset == null)
            throw new InvalidOperationException("The containment graph did not import.");

        // Register the graph once for scenes using the shared FlameEngine prefab.
        var engineAsset = AssetDatabase.LoadAssetAtPath<GameObject>(EnginePrefab);
        if (engineAsset.GetComponent<FlameEngine>().containmentSmokeGraph == asset)
        {
            Debug.Log("Rebuilt the registered Ignis containment graph.", asset);
            return;
        }

        var prefab = PrefabUtility.LoadPrefabContents(EnginePrefab);
        try
        {
            prefab.GetComponent<FlameEngine>().containmentSmokeGraph = asset;
            PrefabUtility.SaveAsPrefabAsset(prefab, EnginePrefab);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefab);
        }
        Debug.Log("Built and registered the Ignis containment graph.", asset);

        void AddBlock(object context, string suffix, string label, string specialInput = null, object specialOutput = null)
        {
            object block = Create("Block.CustomHLSL");
            Invoke(block, "SetSettingValue", "m_BlockName", label);
            Invoke(block, "SetSettingValue", "m_HLSLCode",
                File.ReadAllText($"Assets/VFX/IgnisSmokeContainment{suffix}.hlsl"));
            Invoke(context, "AddChild", block);
            Invoke(block, "ResyncSlots", true);
            var slots = Children(block, "inputSlots");
            if (slots.Length != (specialInput == null ? 4 : 5))
                throw new InvalidOperationException($"Unexpected input signature in {label}.");

            foreach (object slot in slots)
            {
                string name = SlotName(slot).TrimStart('_');
                if (name == specialInput)
                {
                    Invoke(slot, "Link", specialOutput);
                    continue;
                }
                if (!parameters.TryGetValue(name, out object parameter))
                {
                    parameter = Create("VFXParameter");
                    Invoke(parameter, "Init", Read(Read(slot, "property"), "type"));
                    Invoke(parameter, "SetSettingValue", "m_ExposedName", name);
                    Invoke(parameter, "SetSettingValue", "m_Exposed", true);
                    Invoke(graph, "AddChild", parameter);
                    parameters.Add(name, parameter);
                }
                Invoke(slot, "Link", Children(parameter, "outputSlots")[0]);
            }
        }
    }

    private static object FindNode(IEnumerable<object> nodes, long id) => nodes.Single(node =>
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier((UnityEngine.Object)node, out _, out long localId) && localId == id);

    private static string SlotName(object slot) => (string)Read(Read(slot, "property"), "name");

    private static object[] Children(object node, string member) => ((IEnumerable)Read(node, member)).Cast<object>().ToArray();

    private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType("UnityEditor.VFX." + name)).First(type => type != null);

    private static ScriptableObject Create(string name) => ScriptableObject.CreateInstance(FindType(name));

    private static object Read(object node, string name)
    {
        for (Type type = node.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags declared = Instance | BindingFlags.DeclaredOnly;
            if (type.GetProperty(name, declared) is PropertyInfo property)
                return property.GetValue(node);
            if (type.GetField(name, declared) is FieldInfo field)
                return field.GetValue(node);
        }
        throw new MissingMemberException(node.GetType().Name, name);
    }

    private static object Invoke(object node, string name, params object[] arguments)
    {
        foreach (var method in node.GetType().GetMethods(Instance).Where(method => method.Name == name))
        {
            var parameters = method.GetParameters();
            if (parameters.Length < arguments.Length ||
                parameters.Skip(arguments.Length).Any(parameter => !parameter.IsOptional) ||
                arguments.Where((argument, index) => !parameters[index].ParameterType.IsInstanceOfType(argument)).Any())
                continue;

            return method.Invoke(node, arguments.Concat(parameters.Skip(arguments.Length)
                .Select(parameter => parameter.DefaultValue)).ToArray());
        }
        throw new MissingMethodException(node.GetType().Name, name);
    }
}
