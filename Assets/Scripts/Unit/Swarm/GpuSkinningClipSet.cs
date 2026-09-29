using System;
using UnityEngine;

namespace Swarm
{
    [CreateAssetMenu(fileName = "GpuSkinningClipSet", menuName = "Swarm/GPU Skinning Clip Set")]
    public sealed class GpuSkinningClipSet : ScriptableObject
    {
        [Serializable]
        public struct Clip
        {
            public string Name;
            public int StartFrame;
            public int FrameCount;
            public float Length;
        }

        [SerializeField] private Mesh sourceMesh;
        [SerializeField] private int sampleRate = 30;
        [SerializeField] private int boneCount;
        [SerializeField] private Clip[] clips;
        [SerializeField] private Matrix4x4[] boneMatrices;

        public Mesh SourceMesh => sourceMesh;
        public int SampleRate => sampleRate;
        public int BoneCount => boneCount;
        public Clip[] Clips => clips;
        public Matrix4x4[] BoneMatrices => boneMatrices;
        public bool IsValid => sourceMesh != null
                               && boneCount > 0
                               && clips != null
                               && clips.Length > 0
                               && boneMatrices != null
                               && boneMatrices.Length > 0;

        public void Initialize(Mesh mesh, int rate, int bones, Clip[] clipData, Matrix4x4[] matrices)
        {
            sourceMesh = mesh;
            sampleRate = Mathf.Max(1, rate);
            boneCount = Mathf.Max(0, bones);
            clips = clipData ?? Array.Empty<Clip>();
            boneMatrices = matrices ?? Array.Empty<Matrix4x4>();
        }
    }
}
