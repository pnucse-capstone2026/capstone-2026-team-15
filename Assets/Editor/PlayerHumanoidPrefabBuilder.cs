using System;
using System.Collections.Generic;
using System.IO;
using Detection;
using UnityEditor;
using UnityEngine;

public static class PlayerHumanoidPrefabBuilder
{
    private const string SourcePrefabPath = "Assets/StarterAssets/ThirdPersonController/Prefabs/PlayerArmature.prefab";
    private const string OutputPrefabPath = "Assets/Prefabs/PlayerHumanoidStatic.prefab";
    private const string OutputAssetFolder = "Assets/Prefabs/Generated/PlayerHumanoidStatic";

    [MenuItem("Tools/Capstone/Rebuild ECS Player Humanoid")]
    public static void Rebuild()
    {
        EnsureFolder("Assets/Prefabs/Generated");
        EnsureFolder(OutputAssetFolder);

        var bakedParts = BakeSourcePrefab();
        try
        {
            BuildPrefabAsset(bakedParts);
        }
        finally
        {
            foreach (var part in bakedParts)
            {
                part.Dispose();
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PlayerHumanoidPrefabBuilder] ECS humanoid player prefab rebuild complete");
    }

    private static List<BakedMeshPart> BakeSourcePrefab()
    {
        var sourceRoot = PrefabUtility.LoadPrefabContents(SourcePrefabPath);
        try
        {
            var skinnedRenderers = sourceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skinnedRenderers.Length == 0)
            {
                throw new InvalidOperationException("PlayerArmature prefab does not contain a SkinnedMeshRenderer.");
            }

            var bakedParts = new List<BakedMeshPart>(skinnedRenderers.Length);
            foreach (var skinnedRenderer in skinnedRenderers)
            {
                var bakedMesh = new Mesh
                {
                    name = $"{SanitizeName(skinnedRenderer.name)}_Baked"
                };
                skinnedRenderer.BakeMesh(bakedMesh);

                string meshAssetPath = $"{OutputAssetFolder}/{SanitizeName(skinnedRenderer.name)}.asset";
                if (AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath) != null)
                {
                    AssetDatabase.DeleteAsset(meshAssetPath);
                }

                AssetDatabase.CreateAsset(bakedMesh, meshAssetPath);

                bakedParts.Add(new BakedMeshPart
                {
                    Name = skinnedRenderer.name,
                    Mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath),
                    Materials = skinnedRenderer.sharedMaterials,
                    LocalPosition = sourceRoot.transform.InverseTransformPoint(skinnedRenderer.transform.position),
                    LocalRotation = Quaternion.Inverse(sourceRoot.transform.rotation) * skinnedRenderer.transform.rotation,
                    LocalScale = skinnedRenderer.transform.lossyScale
                });
            }

            return bakedParts;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(sourceRoot);
        }
    }

    private static void BuildPrefabAsset(IEnumerable<BakedMeshPart> bakedParts)
    {
        var root = new GameObject("PlayerHumanoidStatic");

        var playerAuthoring = root.AddComponent<PlayerAuthoring>();
        playerAuthoring.enablePlayerTag = true;
        playerAuthoring.enableBillboardTag = false;

        var cameraFollowTarget = root.AddComponent<CameraFollowTargetAuthoring>();
        cameraFollowTarget.isTarget = true;

        var detectionEntity = root.AddComponent<DetectionEntityAuthoring>();
        detectionEntity.factionId = 10;
        detectionEntity.isBase = false;
        detectionEntity.searchRange = 20f;
        detectionEntity.tauntWeight = 1f;
        detectionEntity.targetTauntThreshold = 5f;

        var visualRoot = new GameObject("VisualRoot");
        visualRoot.transform.SetParent(root.transform, false);

        var playerVisual = root.AddComponent<PlayerVisualAuthoring>();
        playerVisual.turnSpeed = 12f;
        playerVisual.visualRoot = visualRoot.transform;

        foreach (var bakedPart in bakedParts)
        {
            var child = new GameObject(bakedPart.Name);
            child.transform.SetParent(visualRoot.transform, false);
            child.transform.localPosition = bakedPart.LocalPosition;
            child.transform.localRotation = bakedPart.LocalRotation;
            child.transform.localScale = bakedPart.LocalScale;

            var meshFilter = child.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = bakedPart.Mesh;

            var meshRenderer = child.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = bakedPart.Materials;
        }

        PrefabUtility.SaveAsPrefabAsset(root, OutputPrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
    }

    private static void EnsureFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath))
        {
            return;
        }

        string parent = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
        string folderName = Path.GetFileName(assetPath);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(folderName))
        {
            throw new InvalidOperationException($"Invalid folder path: {assetPath}");
        }

        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, folderName);
    }

    private static string SanitizeName(string value)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value.Replace(' ', '_');
    }

    private sealed class BakedMeshPart : IDisposable
    {
        public string Name;
        public Mesh Mesh;
        public Material[] Materials;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Vector3 LocalScale;

        public void Dispose()
        {
            Materials = null;
            Mesh = null;
        }
    }
}
