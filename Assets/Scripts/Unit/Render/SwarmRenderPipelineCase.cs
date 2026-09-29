namespace Swarm
{
    /// <summary>
    /// 최종 2x2 실험 조건.
    /// 모든 조건은 동일한 ECS 시뮬레이션 결과를 사용하고,
    /// 렌더링 제출 백엔드와 제출 단위만 변경한다.
    /// </summary>
    public enum SwarmRenderPipelineCase : byte
    {
        RenderMeshIndirect_Integrated = 0,
        RenderMeshIndirect_Split = 1,
        BRG_Integrated = 2,
        BRG_Split = 3
    }

    public enum SwarmRenderBackend : byte
    {
        RenderMeshIndirect = 0,
        BatchRendererGroup = 1
    }

    public enum SwarmSubmissionMode : byte
    {
        Integrated = 0,
        Split = 1
    }

    public enum SwarmCullingMode : byte
    {
        AllVisible = 0
    }

    public static class SwarmRenderPipelineCaseUtility
    {
        public static SwarmRenderBackend GetBackend(
            SwarmRenderPipelineCase renderCase)
        {
            return renderCase == SwarmRenderPipelineCase.BRG_Integrated ||
                   renderCase == SwarmRenderPipelineCase.BRG_Split
                ? SwarmRenderBackend.BatchRendererGroup
                : SwarmRenderBackend.RenderMeshIndirect;
        }

        public static SwarmSubmissionMode GetSubmissionMode(
            SwarmRenderPipelineCase renderCase)
        {
            return renderCase == SwarmRenderPipelineCase.RenderMeshIndirect_Split ||
                   renderCase == SwarmRenderPipelineCase.BRG_Split
                ? SwarmSubmissionMode.Split
                : SwarmSubmissionMode.Integrated;
        }

        public static bool IsBrg(
            SwarmRenderPipelineCase renderCase)
        {
            return GetBackend(renderCase) ==
                   SwarmRenderBackend.BatchRendererGroup;
        }

        public static bool IsRenderMeshIndirect(
            SwarmRenderPipelineCase renderCase)
        {
            return GetBackend(renderCase) ==
                   SwarmRenderBackend.RenderMeshIndirect;
        }

        public static bool IsSplit(
            SwarmRenderPipelineCase renderCase)
        {
            return GetSubmissionMode(renderCase) ==
                   SwarmSubmissionMode.Split;
        }

        public static bool IsIntegrated(
            SwarmRenderPipelineCase renderCase)
        {
            return GetSubmissionMode(renderCase) ==
                   SwarmSubmissionMode.Integrated;
        }

        public static int GetCaseIndex(
            SwarmRenderPipelineCase renderCase)
        {
            return (int)renderCase + 1;
        }

        public static SwarmRenderPipelineCase GetCase(
            SwarmRenderBackend backend,
            SwarmSubmissionMode submissionMode)
        {
            if (backend == SwarmRenderBackend.BatchRendererGroup)
            {
                return submissionMode == SwarmSubmissionMode.Split
                    ? SwarmRenderPipelineCase.BRG_Split
                    : SwarmRenderPipelineCase.BRG_Integrated;
            }

            return submissionMode == SwarmSubmissionMode.Split
                ? SwarmRenderPipelineCase.RenderMeshIndirect_Split
                : SwarmRenderPipelineCase.RenderMeshIndirect_Integrated;
        }

        public static string GetCaseLabel(
            SwarmRenderPipelineCase renderCase)
        {
            return renderCase switch
            {
                SwarmRenderPipelineCase.RenderMeshIndirect_Integrated =>
                    "Case 1 - RenderMeshIndirect / Integrated",

                SwarmRenderPipelineCase.RenderMeshIndirect_Split =>
                    "Case 2 - RenderMeshIndirect / Split",

                SwarmRenderPipelineCase.BRG_Integrated =>
                    "Case 3 - BatchRendererGroup / Integrated",

                SwarmRenderPipelineCase.BRG_Split =>
                    "Case 4 - BatchRendererGroup / Split",

                _ => "Unknown Swarm Render Pipeline Case"
            };
        }
    }
}
