using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// Runtime loader for the OBJ/MTL subset produced by CFRezManager's LithTechObjExporter:
// v / vt / f (v or v/vt), g, usemtl, mtllib, newmtl, Kd, map_Kd.
internal static class RuntimeObjLoader
{
    private sealed class MaterialDef
    {
        public Color BaseColor = Color.gray;
        public string TexturePath;
    }

    private sealed class MeshBucket
    {
        public readonly string Name;
        public string MaterialName = string.Empty;
        public readonly List<int> Indices = new List<int>();
        public readonly Dictionary<long, int> IndexRemap = new Dictionary<long, int>();
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector2> UVs = new List<Vector2>();

        public MeshBucket(string name)
        {
            Name = name;
        }
    }

    public static GameObject Load(string objPath, out Bounds bounds)
    {
        bounds = new Bounds(Vector3.zero, Vector3.one);
        if (string.IsNullOrWhiteSpace(objPath) || !File.Exists(objPath))
        {
            throw new FileNotFoundException("OBJ file not found", objPath);
        }

        string baseDirectory = Path.GetDirectoryName(objPath) ?? string.Empty;
        Dictionary<string, MaterialDef> materials = new Dictionary<string, MaterialDef>(StringComparer.OrdinalIgnoreCase);

        var positions = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var buckets = new List<MeshBucket>();
        MeshBucket current = null;
        string currentGroup = "model";
        string currentMaterial = string.Empty;
        int meshIndex = 0;

        foreach (string rawLine in File.ReadLines(objPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "v":
                    if (parts.Length >= 4 &&
                        TryParseFloat(parts[1], out float x) &&
                        TryParseFloat(parts[2], out float y) &&
                        TryParseFloat(parts[3], out float z))
                    {
                        // WPF Media3D is right-handed (Z toward viewer); Unity is left-handed.
                        positions.Add(new Vector3(x, y, -z));
                    }

                    break;
                case "vt":
                    if (parts.Length >= 3 &&
                        TryParseFloat(parts[1], out float u) &&
                        TryParseFloat(parts[2], out float v))
                    {
                        // The exporter already flipped V to the OBJ/Unity bottom-left convention.
                        texCoords.Add(new Vector2(u, v));
                    }

                    break;
                case "g":
                    currentGroup = parts.Length > 1 ? parts[1] : $"mesh_{meshIndex}";
                    current = null;
                    break;
                case "usemtl":
                    currentMaterial = parts.Length > 1 ? parts[1] : string.Empty;
                    current = null;
                    break;
                case "mtllib":
                    if (parts.Length > 1)
                    {
                        LoadMaterialLibrary(Path.Combine(baseDirectory, parts[1]), materials);
                    }

                    break;
                case "f":
                    if (parts.Length >= 4)
                    {
                        if (current == null)
                        {
                            current = new MeshBucket(string.IsNullOrEmpty(currentGroup) ? $"mesh_{meshIndex}" : currentGroup)
                            {
                                MaterialName = currentMaterial
                            };
                            buckets.Add(current);
                            meshIndex++;
                        }

                        // Fan-triangulate defensively; the exporter emits triangles only.
                        // Emit (a, c, b) to reverse winding, compensating the right-to-left-handed Z flip.
                        for (int i = 2; i < parts.Length - 1; i++)
                        {
                            AddFaceVertex(current, parts[1], positions, texCoords);
                            AddFaceVertex(current, parts[i + 1], positions, texCoords);
                            AddFaceVertex(current, parts[i], positions, texCoords);
                        }
                    }

                    break;
            }
        }

        var root = new GameObject(Path.GetFileNameWithoutExtension(objPath));
        bool boundsInitialized = false;
        foreach (MeshBucket bucket in buckets)
        {
            if (bucket.Indices.Count < 3 || bucket.Vertices.Count == 0)
            {
                continue;
            }

            var mesh = new Mesh
            {
                name = bucket.Name,
                indexFormat = IndexFormat.UInt32
            };
            mesh.SetVertices(bucket.Vertices);
            if (bucket.UVs.Count == bucket.Vertices.Count)
            {
                mesh.SetUVs(0, bucket.UVs);
            }

            mesh.SetTriangles(bucket.Indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var child = new GameObject(bucket.Name);
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CreateMaterial(bucket.MaterialName, materials, baseDirectory);

            if (!boundsInitialized)
            {
                bounds = mesh.bounds;
                boundsInitialized = true;
            }
            else
            {
                bounds.Encapsulate(mesh.bounds);
            }
        }

        return root;
    }

    private static void AddFaceVertex(MeshBucket bucket, string token, List<Vector3> positions, List<Vector2> texCoords)
    {
        string[] indices = token.Split('/');
        if (!TryResolveIndex(indices[0], positions.Count, out int positionIndex))
        {
            return;
        }

        int uvIndex = -1;
        if (indices.Length > 1 && indices[1].Length > 0)
        {
            TryResolveIndex(indices[1], texCoords.Count, out uvIndex);
        }

        // Key on the position/UV pair so shared vertices stay deduplicated per bucket.
        long key = ((long)positionIndex << 32) | (uint)(uvIndex + 1);
        if (bucket.IndexRemap.TryGetValue(key, out int remapped))
        {
            bucket.Indices.Add(remapped);
            return;
        }

        bucket.IndexRemap[key] = bucket.Vertices.Count;
        bucket.Indices.Add(bucket.Vertices.Count);
        bucket.Vertices.Add(positions[positionIndex]);
        bucket.UVs.Add(uvIndex >= 0 ? texCoords[uvIndex] : Vector2.zero);
    }

    private static bool TryResolveIndex(string token, int count, out int index)
    {
        index = -1;
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) || raw == 0)
        {
            return false;
        }

        // OBJ indices are 1-based; negative values are relative to the end of the list.
        index = raw > 0 ? raw - 1 : count + raw;
        return index >= 0 && index < count;
    }

    private static void LoadMaterialLibrary(string mtlPath, Dictionary<string, MaterialDef> materials)
    {
        if (!File.Exists(mtlPath))
        {
            return;
        }

        MaterialDef current = null;
        string baseDirectory = Path.GetDirectoryName(mtlPath) ?? string.Empty;
        foreach (string rawLine in File.ReadLines(mtlPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "newmtl":
                    current = new MaterialDef();
                    materials[parts.Length > 1 ? parts[1] : string.Empty] = current;
                    break;
                case "Kd":
                    if (current != null && parts.Length >= 4 &&
                        TryParseFloat(parts[1], out float r) &&
                        TryParseFloat(parts[2], out float g) &&
                        TryParseFloat(parts[3], out float b))
                    {
                        current.BaseColor = new Color(r, g, b);
                    }

                    break;
                case "map_Kd":
                    if (current != null && parts.Length > 1)
                    {
                        current.TexturePath = Path.Combine(baseDirectory, parts[1].Replace('/', Path.DirectorySeparatorChar));
                    }

                    break;
            }
        }
    }

    private static Material CreateMaterial(string materialName, Dictionary<string, MaterialDef> materials, string baseDirectory)
    {
        MaterialDef def = null;
        if (!string.IsNullOrEmpty(materialName))
        {
            materials.TryGetValue(materialName, out def);
        }

        Material material = CreateMaterialInstance(materialName);
        ApplyTextureAndColor(material, def?.BaseColor ?? Color.gray, LoadTexture(def?.TexturePath));
        return material;
    }

    // Used by the animation package loader, which carries a direct texture path per mesh.
    internal static Material CreateTextureMaterial(string materialName, string texturePath)
    {
        Material material = CreateMaterialInstance(materialName);
        ApplyTextureAndColor(material, Color.gray, LoadTexture(texturePath));
        return material;
    }

    private static Material CreateMaterialInstance(string materialName)
    {
        // Shader.Find only sees shaders included in the player build, and the pipeline's
        // default material is editor-only. A Resources material asset is always shipped.
        Material template = Resources.Load<Material>("CFRezViewerLit");
        Material material;
        if (template != null)
        {
            material = new Material(template);
        }
        else
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException("URP Lit shader missing from the player build.");
            }

            material = new Material(shader);
        }

        material.name = string.IsNullOrEmpty(materialName) ? "default" : materialName;
        return material;
    }

    private static void ApplyTextureAndColor(Material material, Color baseColor, Texture2D texture)
    {
        if (texture != null)
        {
            material.SetTexture("_BaseMap", texture);
            material.SetTexture("_MainTex", texture);
            baseColor = Color.white;
        }

        material.SetColor("_BaseColor", baseColor);
        material.color = baseColor;
        material.SetFloat("_Smoothness", 0.05f);
    }

    internal static Texture2D LoadTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true);
            return texture.LoadImage(File.ReadAllBytes(path)) ? texture : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseFloat(string token, out float value)
    {
        return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
