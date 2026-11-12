using System;
using System.Collections.Generic;
using System.Linq;
using Ignis;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Bakes the selected house's static colliders into one texture shared by all smoke volumes.
public static class IgnisSmokeFieldBaker
{
    private const string Menu = "Tools/Smoke Geometry/Bake Smoke Collision Field";
    private const string ShaderPath = "Assets/VFX/IgnisSmokeFieldBake.compute";
    private const float Voxel = 0.12f;
    private const float Bin = 0.6f;
    private const float Band = 0.8f;
    private const int MaxVoxels = 8 * 1024 * 1024;

    [MenuItem(Menu, true)]
    private static bool CanBake() => !EditorApplication.isPlayingOrWillChangePlaymode &&
        Selection.activeGameObject != null && !EditorUtility.IsPersistent(Selection.activeGameObject);

    [MenuItem(Menu)]
    private static void BakeSelectedHouse()
    {
        GameObject house = Selection.activeGameObject;
        string path = EditorUtility.SaveFilePanelInProject(
            "Save smoke collision field", house.name + " Smoke Collision Field", "asset",
            "Choose where to save the baked field for this house.");
        if (string.IsNullOrEmpty(path))
            return;

        var existing = AssetDatabase.LoadMainAssetAtPath(path);
        if (existing != null && !(existing is SmokeContainmentField))
        {
            Debug.LogError("Choose a new path or an existing smoke collision field.");
            return;
        }

        try
        {
            EditorUtility.DisplayProgressBar("Smoke collision field", "Baking static house colliders", 0.5f);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Texture3D texture = BuildField(house.transform, shader, out Bounds bounds);
            var field = (SmokeContainmentField)existing;
            if (field == null)
            {
                field = ScriptableObject.CreateInstance<SmokeContainmentField>();
                AssetDatabase.CreateAsset(field, path);
            }

            // Replace the texture in place so scene references to this field survive rebakes.
            Texture3D prior = AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                .OfType<Texture3D>().FirstOrDefault();
            if (prior == null)
                AssetDatabase.AddObjectToAsset(texture, field);
            else
            {
                EditorUtility.CopySerialized(texture, prior);
                UnityEngine.Object.DestroyImmediate(texture);
                texture = prior;
                EditorUtility.SetDirty(texture);
            }
            field.Initialize(texture, bounds.min, bounds.size, Voxel);
            EditorUtility.SetDirty(field);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(field);
            Debug.Log($"Baked smoke collision field ({texture.width} × {texture.height} × {texture.depth}).", field);
        }
        catch (Exception error)
        {
            Debug.LogException(error, house);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // Generate the texture independently of asset serialization so the bake can be reused.
    private static Texture3D BuildField(Transform house, ComputeShader shader, out Bounds bounds)
    {
        if (shader == null || !SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            throw new InvalidOperationException("The editor needs the bake shader and a GPU that supports compute and readback.");

        var vertices = new List<Vector4>();
        Physics.SyncTransforms();
        foreach (Collider collider in house.GetComponentsInChildren<Collider>())
        {
            if (!collider.enabled || collider.isTrigger || collider.name == "Hitbox")
                continue;

            if (collider is BoxCollider box)
                AddBox(vertices, box);
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
                AddMesh(vertices, mesh);
        }
        if (vertices.Count == 0)
            throw new InvalidOperationException("The selected object has no enabled solid box or mesh colliders.");

        bounds = new Bounds((Vector3)vertices[0], Vector3.zero);
        foreach (Vector4 vertex in vertices)
            bounds.Encapsulate((Vector3)vertex);
        bounds.Expand(Band * 2f);
        Vector3Int dimensions = Vector3Int.CeilToInt(bounds.size / Voxel);
        long count = (long)dimensions.x * dimensions.y * dimensions.z;
        int limit = SystemInfo.maxTexture3DSize;
        if (count > MaxVoxels || dimensions.x > limit || dimensions.y > limit || dimensions.z > limit)
            throw new InvalidOperationException("The collision field is too large. Select the house geometry without the surrounding terrain.");
        Vector3 minimum = bounds.min;
        Vector3 size = (Vector3)dimensions * Voxel;
        bounds = new Bounds(minimum + size * 0.5f, size);

        Vector3Int bins = Vector3Int.CeilToInt(size / Bin);
        BuildBins(vertices, minimum, bins, out Vector2Int[] ranges, out List<int> indices);
        using var vertexBuffer = new ComputeBuffer(vertices.Count, 16);
        using var rangeBuffer = new ComputeBuffer(ranges.Length, 8);
        using var indexBuffer = new ComputeBuffer(indices.Count, 4);
        vertexBuffer.SetData(vertices);
        rangeBuffer.SetData(ranges);
        indexBuffer.SetData(indices);
        var descriptor = new RenderTextureDescriptor(dimensions.x, dimensions.y, RenderTextureFormat.ARGBHalf, 0)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = dimensions.z,
            enableRandomWrite = true,
            sRGB = false
        };
        var output = new RenderTexture(descriptor);
        try
        {
            if (!output.Create())
                throw new InvalidOperationException("Could not allocate the 3D collision texture.");

            int kernel = shader.FindKernel("Bake");
            shader.SetBuffer(kernel, "Vertices", vertexBuffer);
            shader.SetBuffer(kernel, "Ranges", rangeBuffer);
            shader.SetBuffer(kernel, "Indices", indexBuffer);
            shader.SetTexture(kernel, "Field", output);
            shader.SetVector("FieldMin", minimum);
            shader.SetFloat("VoxelSize", Voxel);
            shader.SetFloat("BinSize", Bin);
            shader.SetFloat("MaxDistance", Band);
            shader.SetInts("Dimensions", dimensions.x, dimensions.y, dimensions.z);
            shader.SetInts("BinDimensions", bins.x, bins.y, bins.z);
            shader.Dispatch(kernel, (dimensions.x + 3) / 4, (dimensions.y + 3) / 4, (dimensions.z + 3) / 4);

            var request = AsyncGPUReadback.Request(output, 0, TextureFormat.RGBAHalf);
            request.WaitForCompletion();
            if (request.hasError)
                throw new InvalidOperationException("Could not read the baked collision texture from the GPU.");

            var bytes = new byte[request.layerDataSize * request.layerCount];
            for (int layer = 0; layer < request.layerCount; layer++)
                NativeArray<byte>.Copy(request.GetData<byte>(layer), 0, bytes,
                    layer * request.layerDataSize, request.layerDataSize);
            var texture = new Texture3D(dimensions.x, dimensions.y, dimensions.z, TextureFormat.RGBAHalf, false)
            {
                name = "Smoke distance and surface normals",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixelData(bytes, 0);
            texture.Apply(false, false);
            return texture;
        }
        finally
        {
            output.Release();
            UnityEngine.Object.DestroyImmediate(output);
        }
    }

    // Store only the triangles near each bin so each voxel checks local geometry.
    private static void BuildBins(List<Vector4> vertices, Vector3 minimum, Vector3Int dimensions,
        out Vector2Int[] ranges, out List<int> indices)
    {
        var buckets = new List<int>[dimensions.x * dimensions.y * dimensions.z];
        Vector3 padding = Vector3.one * Band;
        for (int i = 0; i < vertices.Count; i += 3)
        {
            Vector3 a = vertices[i], b = vertices[i + 1], c = vertices[i + 2];
            Vector3Int first = Cell(Vector3.Min(a, Vector3.Min(b, c)) - minimum - padding, dimensions);
            Vector3Int last = Cell(Vector3.Max(a, Vector3.Max(b, c)) - minimum + padding, dimensions);
            for (int z = first.z; z <= last.z; z++)
            for (int y = first.y; y <= last.y; y++)
            for (int x = first.x; x <= last.x; x++)
            {
                int bin = x + dimensions.x * (y + dimensions.y * z);
                (buckets[bin] ??= new List<int>()).Add(i / 3);
            }
        }

        ranges = new Vector2Int[buckets.Length];
        indices = new List<int>();
        for (int i = 0; i < buckets.Length; i++)
        {
            ranges[i] = new Vector2Int(indices.Count, buckets[i]?.Count ?? 0);
            if (buckets[i] != null)
                indices.AddRange(buckets[i]);
        }
    }

    private static Vector3Int Cell(Vector3 point, Vector3Int dimensions)
    {
        Vector3Int cell = Vector3Int.FloorToInt(point / Bin);
        return new Vector3Int(Mathf.Clamp(cell.x, 0, dimensions.x - 1),
            Mathf.Clamp(cell.y, 0, dimensions.y - 1), Mathf.Clamp(cell.z, 0, dimensions.z - 1));
    }

    // Convert the mesh triangles to world space, the space used by Ignis particles.
    private static void AddMesh(List<Vector4> vertices, MeshCollider collider)
    {
        Mesh mesh = collider.sharedMesh;
        Vector3[] points = mesh.vertices;
        int[] triangles = mesh.triangles;
        Matrix4x4 transform = collider.transform.localToWorldMatrix;
        for (int i = 0; i < triangles.Length; i += 3)
            AddTriangle(vertices, transform.MultiplyPoint3x4(points[triangles[i]]),
                transform.MultiplyPoint3x4(points[triangles[i + 1]]),
                transform.MultiplyPoint3x4(points[triangles[i + 2]]));
    }

    // Represent the six box faces as triangles for the shared bake shader.
    private static void AddBox(List<Vector4> vertices, BoxCollider collider)
    {
        var points = new Vector3[8];
        for (int i = 0; i < points.Length; i++)
        {
            var sign = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            points[i] = collider.transform.TransformPoint(collider.center + Vector3.Scale(collider.size * 0.5f, sign));
        }
        int[] faces = { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4,
            2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 };
        for (int i = 0; i < faces.Length; i += 3)
            AddTriangle(vertices, points[faces[i]], points[faces[i + 1]], points[faces[i + 2]]);
    }

    private static void AddTriangle(List<Vector4> vertices, Vector3 a, Vector3 b, Vector3 c)
    {
        if (Vector3.Cross(b - a, c - a).sqrMagnitude <= 1e-12f)
            return;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
    }
}
