Shader "Swarm/RenderMeshIndirect GPU Skinning Unlit"
{
    Properties
    {
        [MainTexture]
        _BaseMap("Base Map", 2D) = "white" {}

        [MainColor]
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)

        _DebugClipColor("Debug Clip Color", Float) = 0

        [HideInInspector]
        _SkinningEnabled("Skinning Enabled", Float) = 1

        [HideInInspector]
        _SkinningBoneCount("Skinning Bone Count", Float) = 1

        [HideInInspector]
        _SkinningClipCount("Skinning Clip Count", Float) = 1

        [HideInInspector]
        _InstanceOffset("Instance Offset", Int) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardUnlit"

            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shader/Swarm/SwarmFactionColor.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)

                float4 _BaseMap_ST;
                float4 _BaseColor;

                float _DebugClipColor;
                float _SkinningEnabled;
                float _SkinningBoneCount;
                float _SkinningClipCount;

                int _InstanceOffset;
                float3 _InstanceOffsetPadding;

            CBUFFER_END

            struct SoldierAnimationPayload
            {
                int ClipIndex;
                float NormalizedTime;
                float PlaybackSpeed;
                float Padding;
            };

            struct SkinningClipInfo
            {
                int StartFrame;
                int FrameCount;
                float Length;
                float Padding;
            };

            StructuredBuffer<float4x4>
                _VisibleMatrices;

            StructuredBuffer<SoldierAnimationPayload>
                _SoldierAnimationPayloads;

            StructuredBuffer<float4x4>
                _SkinningMatrices;

            StructuredBuffer<SkinningClipInfo>
                _SkinningClipInfos;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;

                float4 blendWeights : BLENDWEIGHTS;
                uint4 blendIndices : BLENDINDICES;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 animation : TEXCOORD1;
            };

            float GetWeightSum(
                float4 weights)
            {
                return
                    weights.x +
                    weights.y +
                    weights.z +
                    weights.w;
            }

            int GetSafeClipIndex(
                int rawClipIndex)
            {
                int clipCount =
                    max(
                        1,
                        (int)round(
                            _SkinningClipCount));

                return clamp(
                    rawClipIndex,
                    0,
                    clipCount - 1);
            }

            uint GetSafeBoneIndex(
                uint rawBoneIndex)
            {
                int boneCount =
                    max(
                        1,
                        (int)round(
                            _SkinningBoneCount));

                return min(
                    rawBoneIndex,
                    (uint)max(
                        0,
                        boneCount - 1));
            }

            float4 ApplyGpuSkinningPosition(
                float4 positionOS,
                float4 weights,
                uint4 indices,
                SoldierAnimationPayload animation)
            {
                if (_SkinningEnabled < 0.5)
                {
                    return positionOS;
                }

                float weightSum =
                    GetWeightSum(
                        weights);

                if (weightSum <= 0.0001)
                {
                    return positionOS;
                }

                int boneCount =
                    max(
                        1,
                        (int)round(
                            _SkinningBoneCount));

                int clipIndex =
                    GetSafeClipIndex(
                        animation.ClipIndex);

                SkinningClipInfo clip =
                    _SkinningClipInfos[
                        clipIndex];

                int frameCount =
                    max(
                        clip.FrameCount,
                        1);

                float normalizedTime =
                    frac(
                        animation.NormalizedTime);

                int frame =
                    min(
                        (int)floor(
                            normalizedTime *
                            frameCount),
                        frameCount - 1);

                int matrixBase =
                    (
                        clip.StartFrame +
                        frame
                    ) *
                    boneCount;

                uint boneIndex0 =
                    GetSafeBoneIndex(
                        indices.x);

                uint boneIndex1 =
                    GetSafeBoneIndex(
                        indices.y);

                uint boneIndex2 =
                    GetSafeBoneIndex(
                        indices.z);

                uint boneIndex3 =
                    GetSafeBoneIndex(
                        indices.w);

                float4 skinned =
                    float4(
                        0,
                        0,
                        0,
                        0);

                skinned +=
                    mul(
                        _SkinningMatrices[
                            matrixBase +
                            boneIndex0],
                        positionOS) *
                    weights.x;

                skinned +=
                    mul(
                        _SkinningMatrices[
                            matrixBase +
                            boneIndex1],
                        positionOS) *
                    weights.y;

                skinned +=
                    mul(
                        _SkinningMatrices[
                            matrixBase +
                            boneIndex2],
                        positionOS) *
                    weights.z;

                skinned +=
                    mul(
                        _SkinningMatrices[
                            matrixBase +
                            boneIndex3],
                        positionOS) *
                    weights.w;

                skinned.w = 1.0;

                return skinned;
            }

            float4 GetClipDebugColor(
                float clipIndex)
            {
                if (clipIndex < 0.5)
                {
                    return float4(
                        0.65,
                        0.65,
                        0.65,
                        1.0);
                }

                if (clipIndex < 1.5)
                {
                    return float4(
                        0.35,
                        0.75,
                        1.00,
                        1.0);
                }

                if (clipIndex < 2.5)
                {
                    return float4(
                        0.35,
                        1.00,
                        0.45,
                        1.0);
                }

                return float4(
                    1.00,
                    0.35,
                    0.25,
                    1.0);
            }

            Varyings Vert(
                Attributes input,
                uint svInstanceID : SV_InstanceID)
            {
                uint instanceIndex =
                    (uint)max(
                        0,
                        _InstanceOffset) +
                    svInstanceID;

                float4x4 instanceMatrix =
                    _VisibleMatrices[
                        instanceIndex];

                SoldierAnimationPayload animation =
                    _SoldierAnimationPayloads[
                        instanceIndex];

                float4 finalPositionOS =
                    ApplyGpuSkinningPosition(
                        input.positionOS,
                        input.blendWeights,
                        input.blendIndices,
                        animation);

                float3 positionWS =
                    mul(
                        instanceMatrix,
                        finalPositionOS).xyz;

                Varyings output;

                output.positionCS =
                    TransformWorldToHClip(
                        positionWS);

                output.uv =
                    TRANSFORM_TEX(
                        input.uv,
                        _BaseMap);

                output.animation =
                    float4(
                        animation.ClipIndex,
                        animation.NormalizedTime,
                        animation.PlaybackSpeed,
                        animation.Padding);

                return output;
            }

            half4 Frag(
                Varyings input) : SV_Target
            {
                half4 baseMap =
                    SAMPLE_TEXTURE2D(
                        _BaseMap,
                        sampler_BaseMap,
                        input.uv);

                half4 color =
                    baseMap *
                    _BaseColor *
                    GetSwarmFactionColor(
                        input.animation.w);

                if (_DebugClipColor > 0.5)
                {
                    color *=
                        GetClipDebugColor(
                            input.animation.x);
                }

                return color;
            }

            ENDHLSL
        }
    }

    FallBack Off
}
