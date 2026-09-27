using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

// Runtime loader for the .cfan animation package written by CFRezManager's
// LithTechAnimPackageExporter: skeleton bind poses, skinned meshes, and animation
// tracks. Builds a bone hierarchy with SkinnedMeshRenderers and legacy runtime
// AnimationClips so animated LTB models can be previewed with track switching.
//
// The package stores data in the source coordinate space; this loader applies the
// same Z flip as RuntimeObjLoader (positions (x, y, -z), quaternions (-x, -y, z, w),
// matrices conjugated by diag(1,1,-1,1), reversed triangle winding).
internal static class RuntimeAnimPackageLoader
{
    private sealed class NodeDef
    {
        public string Name;
        public int ParentIndex;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public Matrix4x4 BindPose;
        public string Path;
    }

    private sealed class ChannelDef
    {
        public Vector3[] Positions;
        public Quaternion[] Rotations;
    }

    public sealed class LoadResult
    {
        public GameObject Root;
        public Bounds Bounds;
        public Animation Animation;
        public Transform[] Bones;
        public int AnimationCount;
    }

    public static LoadResult Load(string packagePath)
    {
        byte[] bytes = File.ReadAllBytes(packagePath);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        if (reader.ReadByte() != (byte)'C' || reader.ReadByte() != (byte)'F' ||
            reader.ReadByte() != (byte)'R' || reader.ReadByte() != (byte)'A')
        {
            throw new InvalidDataException("Not a CFRA animation package.");
        }

        uint version = reader.ReadUInt32();
        if (version != 1)
        {
            throw new InvalidDataException("Unsupported CFRA version " + version);
        }

        NodeDef[] nodes = ReadSkeleton(reader);
        string baseDirectory = Path.GetDirectoryName(packagePath) ?? string.Empty;
        var root = new GameObject(Path.GetFileNameWithoutExtension(packagePath));

        Transform[] bones = BuildBoneHierarchy(root, nodes);
        Bounds bounds = ReadMeshes(reader, root, nodes, bones, baseDirectory);
        Animation animation = ReadAnimations(reader, root, nodes);

        return new LoadResult
        {
            Root = root,
            Bounds = bounds,
            Animation = animation,
            Bones = bones,
            AnimationCount = animation != null ? animation.GetClipCount() : 0
        };
    }

    private static NodeDef[] ReadSkeleton(BinaryReader reader)
    {
        int nodeCount = (int)reader.ReadUInt32();
        if (nodeCount <= 0 || nodeCount > 4096)
        {
            throw new InvalidDataException("Invalid node count " + nodeCount);
        }

        var nodes = new NodeDef[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            var node = new NodeDef
            {
                Name = ReadString(reader),
                ParentIndex = reader.ReadInt32(),
                Position = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                Scale = ReadVector3(reader),
                BindPose = ReadMatrix(reader)
            };

            // Z flip: mirror translations along Z, negate the non-Z quaternion components.
            node.Position = new Vector3(node.Position.x, node.Position.y, -node.Position.z);
            node.Rotation = new Quaternion(-node.Rotation.x, -node.Rotation.y, node.Rotation.z, node.Rotation.w);
            node.BindPose = ConjugateMirrorZ(node.BindPose);

            if (node.ParentIndex >= i)
            {
                throw new InvalidDataException("Node parent must precede the node.");
            }

            node.Path = node.ParentIndex < 0 ? node.Name : nodes[node.ParentIndex].Path + "/" + node.Name;
            nodes[i] = node;
        }

        return nodes;
    }

    private static Transform[] BuildBoneHierarchy(GameObject root, NodeDef[] nodes)
    {
        var bones = new Transform[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            var boneObject = new GameObject(nodes[i].Name);
            Transform bone = boneObject.transform;
            bone.SetParent(nodes[i].ParentIndex < 0 ? root.transform : bones[nodes[i].ParentIndex], false);
            bone.localPosition = nodes[i].Position;
            bone.localRotation = nodes[i].Rotation;
            bone.localScale = nodes[i].Scale;
            bones[i] = bone;
        }

        return bones;
    }

    private static Bounds ReadMeshes(BinaryReader reader, GameObject root, NodeDef[] nodes, Transform[] bones, string baseDirectory)
    {
        // Make sure 4-influence skins are not truncated by the active quality level.
        QualitySettings.skinWeights = SkinWeights.FourBones;

        var bindPoses = new Matrix4x4[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            bindPoses[i] = nodes[i].BindPose;
        }

        int meshCount = (int)reader.ReadUInt32();
        if (meshCount > 4096)
        {
            throw new InvalidDataException("Invalid mesh count " + meshCount);
        }

        var bounds = new Bounds(Vector3.zero, Vector3.one);
        bool boundsInitialized = false;

        for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            string meshName = ReadString(reader);
            string textureFileName = ReadString(reader);
            int vertexCount = (int)reader.ReadUInt32();
            int indexCount = (int)reader.ReadUInt32();
            if (vertexCount <= 0 || vertexCount > 10_000_000 || indexCount < 0 || indexCount > 30_000_000)
            {
                throw new InvalidDataException("Invalid mesh size.");
            }

            var vertices = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 v = ReadVector3(reader);
                vertices[i] = new Vector3(v.x, v.y, -v.z);
            }

            Vector3[] normals = null;
            if (reader.ReadBoolean())
            {
                normals = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 n = ReadVector3(reader);
                    normals[i] = new Vector3(n.x, n.y, -n.z);
                }
            }

            Vector2[] uvs = null;
            if (reader.ReadBoolean())
            {
                uvs = new Vector2[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    uvs[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                }
            }

            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                indices[i] = (int)reader.ReadUInt32();
            }

            var weights = new BoneWeight[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var weight = new BoneWeight
                {
                    boneIndex0 = reader.ReadByte(),
                    boneIndex1 = reader.ReadByte(),
                    boneIndex2 = reader.ReadByte(),
                    boneIndex3 = reader.ReadByte(),
                    weight0 = reader.ReadSingle(),
                    weight1 = reader.ReadSingle(),
                    weight2 = reader.ReadSingle(),
                    weight3 = reader.ReadSingle()
                };
                weights[i] = NormalizeWeight(weight);
            }

            var mesh = new Mesh
            {
                name = meshName,
                indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(new List<Vector3>(vertices));
            if (normals != null)
            {
                mesh.SetNormals(new List<Vector3>(normals));
            }

            if (uvs != null)
            {
                mesh.SetUVs(0, new List<Vector2>(uvs));
            }

            // Reverse winding to compensate the right-to-left-handed Z flip.
            var flipped = new int[indexCount];
            int usableIndexCount = indexCount - indexCount % 3;
            for (int i = 0; i < usableIndexCount; i += 3)
            {
                flipped[i] = indices[i];
                flipped[i + 1] = indices[i + 2];
                flipped[i + 2] = indices[i + 1];
            }

            mesh.SetTriangles(flipped, 0);
            if (normals == null)
            {
                mesh.RecalculateNormals();
            }

            mesh.boneWeights = weights;
            mesh.bindposes = bindPoses;
            mesh.RecalculateBounds();

            var meshObject = new GameObject(string.IsNullOrEmpty(meshName) ? $"mesh_{meshIndex}" : meshName);
            meshObject.transform.SetParent(root.transform, false);
            SkinnedMeshRenderer renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[0];
            // The animated pose can leave the bind-pose bounds; skip per-frame bounds updates.
            renderer.updateWhenOffscreen = true;
            renderer.sharedMaterial = RuntimeObjLoader.CreateTextureMaterial(
                meshName,
                string.IsNullOrEmpty(textureFileName) ? null : Path.Combine(baseDirectory, textureFileName));

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

        return bounds;
    }

    private static Animation ReadAnimations(BinaryReader reader, GameObject root, NodeDef[] nodes)
    {
        int animationCount = (int)reader.ReadUInt32();
        if (animationCount > 1024)
        {
            throw new InvalidDataException("Invalid animation count " + animationCount);
        }

        if (animationCount == 0)
        {
            return null;
        }

        Animation animation = root.AddComponent<Animation>();
        for (int animationIndex = 0; animationIndex < animationCount; animationIndex++)
        {
            string name = ReadString(reader);
            int keyCount = (int)reader.ReadUInt32();
            if (keyCount <= 0 || keyCount > 100_000)
            {
                throw new InvalidDataException("Invalid keyframe count " + keyCount);
            }

            var keyTimes = new float[keyCount];
            for (int i = 0; i < keyCount; i++)
            {
                keyTimes[i] = reader.ReadSingle();
            }

            var channels = new ChannelDef[nodes.Length];
            for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                bool hasPositions = reader.ReadBoolean();
                bool hasRotations = reader.ReadBoolean();
                var channel = new ChannelDef();

                if (hasPositions)
                {
                    channel.Positions = new Vector3[keyCount];
                    for (int key = 0; key < keyCount; key++)
                    {
                        Vector3 p = ReadVector3(reader);
                        channel.Positions[key] = new Vector3(p.x, p.y, -p.z);
                    }
                }

                if (hasRotations)
                {
                    channel.Rotations = new Quaternion[keyCount];
                    for (int key = 0; key < keyCount; key++)
                    {
                        Quaternion q = ReadQuaternion(reader);
                        channel.Rotations[key] = new Quaternion(-q.x, -q.y, q.z, q.w);
                    }

                    EnsureQuaternionContinuity(channel.Rotations);
                }

                channels[nodeIndex] = channel;
            }

            AnimationClip clip = BuildClip(string.IsNullOrEmpty(name) ? $"anim_{animationIndex}" : name, keyTimes, channels, nodes);
            animation.AddClip(clip, $"anim_{animationIndex}");
        }

        animation.playAutomatically = false;
        return animation;
    }

    private static AnimationClip BuildClip(string name, float[] keyTimes, ChannelDef[] channels, NodeDef[] nodes)
    {
        var clip = new AnimationClip
        {
            name = name,
            legacy = true,
            wrapMode = WrapMode.Loop
        };

        for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            ChannelDef channel = channels[nodeIndex];
            if (channel == null)
            {
                continue;
            }

            string path = nodes[nodeIndex].Path;
            if (channel.Positions != null)
            {
                SetPositionCurves(clip, path, keyTimes, channel.Positions);
            }

            if (channel.Rotations != null)
            {
                SetRotationCurves(clip, path, keyTimes, channel.Rotations);
            }
        }

        clip.EnsureQuaternionContinuity();
        return clip;
    }

    private static void SetPositionCurves(AnimationClip clip, string path, float[] keyTimes, Vector3[] positions)
    {
        var curveX = new AnimationCurve();
        var curveY = new AnimationCurve();
        var curveZ = new AnimationCurve();
        for (int i = 0; i < keyTimes.Length; i++)
        {
            curveX.AddKey(new Keyframe(keyTimes[i], positions[i].x));
            curveY.AddKey(new Keyframe(keyTimes[i], positions[i].y));
            curveZ.AddKey(new Keyframe(keyTimes[i], positions[i].z));
        }

        clip.SetCurve(path, typeof(Transform), "localPosition.x", curveX);
        clip.SetCurve(path, typeof(Transform), "localPosition.y", curveY);
        clip.SetCurve(path, typeof(Transform), "localPosition.z", curveZ);
    }

    private static void SetRotationCurves(AnimationClip clip, string path, float[] keyTimes, Quaternion[] rotations)
    {
        var curveX = new AnimationCurve();
        var curveY = new AnimationCurve();
        var curveZ = new AnimationCurve();
        var curveW = new AnimationCurve();
        for (int i = 0; i < keyTimes.Length; i++)
        {
            curveX.AddKey(new Keyframe(keyTimes[i], rotations[i].x));
            curveY.AddKey(new Keyframe(keyTimes[i], rotations[i].y));
            curveZ.AddKey(new Keyframe(keyTimes[i], rotations[i].z));
            curveW.AddKey(new Keyframe(keyTimes[i], rotations[i].w));
        }

        clip.SetCurve(path, typeof(Transform), "localRotation.x", curveX);
        clip.SetCurve(path, typeof(Transform), "localRotation.y", curveY);
        clip.SetCurve(path, typeof(Transform), "localRotation.z", curveZ);
        clip.SetCurve(path, typeof(Transform), "localRotation.w", curveW);
    }

    private static void EnsureQuaternionContinuity(Quaternion[] rotations)
    {
        for (int i = 1; i < rotations.Length; i++)
        {
            Quaternion previous = rotations[i - 1];
            Quaternion current = rotations[i];
            float dot = previous.x * current.x + previous.y * current.y +
                        previous.z * current.z + previous.w * current.w;
            if (dot < 0f)
            {
                rotations[i] = new Quaternion(-current.x, -current.y, -current.z, -current.w);
            }
        }
    }

    private static BoneWeight NormalizeWeight(BoneWeight weight)
    {
        float sum = weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3;
        if (sum <= 0.0001f)
        {
            return new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        }

        weight.weight0 /= sum;
        weight.weight1 /= sum;
        weight.weight2 /= sum;
        weight.weight3 /= sum;
        return weight;
    }

    private static Matrix4x4 ConjugateMirrorZ(Matrix4x4 m)
    {
        // (F * M * F) with F = diag(1, 1, -1, 1): negate entries with exactly one index equal to 2.
        m[0, 2] = -m[0, 2];
        m[1, 2] = -m[1, 2];
        m[2, 0] = -m[2, 0];
        m[2, 1] = -m[2, 1];
        m[2, 3] = -m[2, 3];
        m[3, 2] = -m[3, 2];
        return m;
    }

    private static string ReadString(BinaryReader reader)
    {
        int byteCount = reader.ReadInt32();
        if (byteCount < 0 || byteCount > 64 * 1024)
        {
            throw new InvalidDataException("Invalid string length " + byteCount);
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(byteCount));
    }

    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static Quaternion ReadQuaternion(BinaryReader reader)
    {
        return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static Matrix4x4 ReadMatrix(BinaryReader reader)
    {
        // Package matrices are row-major 4x4 with the translation column at [3], [7], [11];
        // Matrix4x4[row, column] indexing matches that layout directly.
        var matrix = new Matrix4x4();
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                matrix[row, column] = reader.ReadSingle();
            }
        }

        return matrix;
    }
}
