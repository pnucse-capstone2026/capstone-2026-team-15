namespace Swarm
{
    public struct SwarmBRGStats
    {
        public SwarmRenderPipelineCase RenderCase;
        public SwarmRenderBackend Backend;
        public SwarmSubmissionMode SubmissionMode;
        public SwarmCullingMode CullingMode;

        public int CaseIndex;
        public int InstanceCount;
        public int InstanceCapacity;
        public int VisibleInstanceCount;

        public int BatchCount;
        public int DrawRangeCount;
        public int DrawCommandCount;

        public int MeshCount;
        public int MaterialCount;

        public int InstanceLogicalStrideBytes;
        public int InstanceBufferSizeBytes;
        public int UploadedBytesPerFrame;

        public int FrameIndex;
        public bool IsValid;

        public static SwarmBRGStats CreateDefault()
        {
            var stats = new SwarmBRGStats
            {
                CullingMode = SwarmCullingMode.AllVisible,
                InstanceLogicalStrideBytes =
                    SwarmBrgInstanceProperties.LogicalPackedBytesPerInstance
            };

            stats.ApplySubmissionMode(
                SwarmSubmissionMode.Integrated);

            return stats;
        }

        public void ApplySubmissionMode(
            SwarmSubmissionMode submissionMode)
        {
            Backend = SwarmRenderBackend.BatchRendererGroup;
            SubmissionMode = submissionMode;
            CullingMode = SwarmCullingMode.AllVisible;

            RenderCase = SwarmRenderPipelineCaseUtility.GetCase(
                Backend,
                submissionMode);

            CaseIndex =
                SwarmRenderPipelineCaseUtility.GetCaseIndex(RenderCase);
        }

        public void SetInstanceInfo(
            int instanceCount,
            int instanceCapacity,
            int instanceBufferSizeBytes)
        {
            InstanceCount = instanceCount;
            InstanceCapacity = instanceCapacity;
            VisibleInstanceCount = instanceCount;

            InstanceLogicalStrideBytes =
                SwarmBrgInstanceProperties.LogicalPackedBytesPerInstance;

            InstanceBufferSizeBytes = instanceBufferSizeBytes;
            UploadedBytesPerFrame =
                instanceCount * InstanceLogicalStrideBytes;
        }

        public void SetSubmissionInfo(
            int batchCount,
            int drawRangeCount,
            int drawCommandCount)
        {
            BatchCount = batchCount;
            DrawRangeCount = drawRangeCount;
            DrawCommandCount = drawCommandCount;
        }

        public void SetResourceInfo(
            int meshCount,
            int materialCount)
        {
            MeshCount = meshCount;
            MaterialCount = materialCount;
        }

        public void MarkValid(
            int frameIndex)
        {
            FrameIndex = frameIndex;
            IsValid = true;
        }

        public void MarkInvalid(
            int frameIndex)
        {
            FrameIndex = frameIndex;
            IsValid = false;

            InstanceCount = 0;
            VisibleInstanceCount = 0;
            DrawRangeCount = 0;
            DrawCommandCount = 0;
            UploadedBytesPerFrame = 0;
        }
    }
}
