using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Prepares the imported commander vehicle for Play mode. The Ford F250 export is ~7,400
/// small meshes, which would cost thousands of draw calls, so at startup they are merged into
/// one mesh per material. <see cref="materialSwaps"/> repaints materials (the white body
/// becomes red); a swap without a replacement drops that geometry (the export's invisible
/// edge lines). The original model stays untouched in the scene for editing.
/// </summary>
public sealed class CommanderVehicleModel : MonoBehaviour
{
    [Serializable]
    public struct MaterialSwap
    {
        public Material source;
        [Tooltip("Leave empty to remove everything drawn with the source material.")]
        public Material replacement;
    }

    [Tooltip("The imported model to merge (a child of this object).")]
    [SerializeField] private Transform model;
    [SerializeField] private MaterialSwap[] materialSwaps = Array.Empty<MaterialSwap>();

    private void Awake()
    {
        if (model == null)
            return;

        var groups = new Dictionary<Material, List<CombineInstance>>();
        var castsShadows = new Dictionary<Material, bool>();
        Matrix4x4 toLocal = transform.worldToLocalMatrix;

        foreach (MeshRenderer meshRenderer in model.GetComponentsInChildren<MeshRenderer>())
        {
            MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable)
                continue;

            Material[] materials = meshRenderer.sharedMaterials;
            for (int sub = 0; sub < mesh.subMeshCount && sub < materials.Length; sub++)
            {
                if (!TrySwap(materials[sub], out Material material))
                    continue;
                if (!groups.TryGetValue(material, out List<CombineInstance> list))
                    groups[material] = list = new List<CombineInstance>();
                list.Add(new CombineInstance
                {
                    mesh = mesh,
                    subMeshIndex = sub,
                    transform = toLocal * meshRenderer.localToWorldMatrix,
                });
                castsShadows[material] = meshRenderer.shadowCastingMode != ShadowCastingMode.Off;
            }
        }

        if (groups.Count == 0)
        {
            Debug.LogWarning("[Commander] Vehicle meshes are not readable; using the model unmerged.", this);
            return;
        }

        var merged = new GameObject("Merged Vehicle").transform;
        merged.SetParent(transform, false);
        foreach (KeyValuePair<Material, List<CombineInstance>> group in groups)
        {
            var mesh = new Mesh { name = group.Key.name, indexFormat = IndexFormat.UInt32 };
            mesh.CombineMeshes(group.Value.ToArray(), true, true);
            mesh.RecalculateBounds();

            var part = new GameObject(group.Key.name, typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(merged, false);
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer partRenderer = part.GetComponent<MeshRenderer>();
            partRenderer.sharedMaterial = group.Key;
            partRenderer.shadowCastingMode = castsShadows[group.Key] ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        model.gameObject.SetActive(false);
    }

    private bool TrySwap(Material source, out Material result)
    {
        result = source;
        if (source == null)
            return false;
        foreach (MaterialSwap swap in materialSwaps)
        {
            if (swap.source == source)
            {
                result = swap.replacement;
                return result != null;
            }
        }
        return true;
    }
}
