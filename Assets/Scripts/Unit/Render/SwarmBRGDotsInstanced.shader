Shader "Swarm/BRG DOTS Instanced Unlit"
{
    Properties
    {
        // ==========================================================
        // Basic Material Properties
        // ==========================================================

        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)

        _YOffset ("Y Offset", Float) = 0
        _DebugClipColor ("Debug Clip Color", Float) = 0

        // ==========================================================
        // GPU Skinning Material Properties
        // ==========================================================
        //
        // SRP Batcher 호환을 위해 UnityPerMaterial CBUFFER에 들어가는 값은
        // Properties에도 선언해 둔다.

        [HideInInspector] _SkinningEnabled ("Skinning Enabled", Float) = 0
        [HideInInspector] _SkinningBoneCount ("Skinning Bone Count", Float) = 1
        [HideInInspector] _SkinningClipCount ("Skinning Clip Count", Float) = 1
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

            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ SWARM_GPU_SKINNING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityDOTSInstancing.hlsl"
            #include "Assets/Shader/Swarm/SwarmFactionColor.hlsl"

            // ======================================================
            // Texture / Material Properties
            // ======================================================

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;

                float _YOffset;
                float _DebugClipColor;

                float _SkinningEnabled;
                float _SkinningBoneCount;
                float _SkinningClipCount;
            CBUFFER_END

            // ======================================================
            // GPU Skinning Buffers
            // ======================================================
            //
            // D3D12에서는 shader variant에 StructuredBuffer가 존재하면
            // 실제 분기에서 사용하지 않아도 SRV 바인딩을 요구할 수 있다.
            // 따라서 GPU skinning buffer는 SWARM_GPU_SKINNING_ON variant에서만 선언한다.

            #if defined(SWARM_GPU_SKINNING_ON)

            struct SkinningClipInfo
            {
                int StartFrame;
                int FrameCount;
                float Length;
                float Padding;
            };

            StructuredBuffer<float4x4> _SkinningMatrices;
            StructuredBuffer<SkinningClipInfo> _SkinningClipInfos;

            #endif

            // ======================================================
            // DOTS Instancing Properties
            // ======================================================

            #ifdef UNITY_DOTS_INSTANCING_ENABLED

                UNITY_DOTS_INSTANCING_START(UserPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_REQUIRED(float4, PositionYaw)
                    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_REQUIRED(float4, OffsetScale)
                    UNITY_DOTS_INSTANCED_PROP_OVERRIDE_REQUIRED(float4, Animation)
                UNITY_DOTS_INSTANCING_END(UserPropertyMetadata)

            #endif

            // ======================================================
            // Vertex Input / Output
            // ======================================================

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float2 uv           : TEXCOORD0;

                #if defined(SWARM_GPU_SKINNING_ON)
                    float4 blendWeights : BLENDWEIGHTS;
                    uint4 blendIndices  : BLENDINDICES;
                #endif

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 animation  : TEXCOORD1;

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // ======================================================
            // DOTS Property Access Helpers
            // ======================================================

            float4 GetPositionYaw()
            {
                #ifdef UNITY_DOTS_INSTANCING_ENABLED
                    return UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, PositionYaw);
                #else
                    return float4(0.0, 0.0, 0.0, 0.0);
                #endif
            }

            float4 GetOffsetScale()
            {
                #ifdef UNITY_DOTS_INSTANCING_ENABLED
                    return UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, OffsetScale);
                #else
                    return float4(0.0, 0.0, 0.0, 1.0);
                #endif
            }

            float4 GetAnimation()
            {
                #ifdef UNITY_DOTS_INSTANCING_ENABLED
                    return UNITY_ACCESS_DOTS_INSTANCED_PROP(float4, Animation);
                #else
                    return float4(0.0, 0.0, 1.0, 0.0);
                #endif
            }

            // ======================================================
            // Transform Helpers
            // ======================================================

            float3 RotateY(float3 value, float yaw)
            {
                float s = sin(yaw);
                float c = cos(yaw);

                return float3(
                    value.x * c + value.z * s,
                    value.y,
                    -value.x * s + value.z * c
                );
            }

            // ======================================================
            // GPU Skinning
            // ======================================================

            #if defined(SWARM_GPU_SKINNING_ON)

            float GetWeightSum(float4 weights)
            {
                return weights.x + weights.y + weights.z + weights.w;
            }

            int GetSafeClipIndex(float clipIndexValue)
            {
                int clipCount =
                    max(1, (int)round(_SkinningClipCount));

                int clipIndex =
                    (int)round(clipIndexValue);

                return clamp(
                    clipIndex,
                    0,
                    clipCount - 1);
            }

            uint GetSafeBoneIndex(uint rawBoneIndex)
            {
                int boneCount =
                    max(1, (int)round(_SkinningBoneCount));

                uint maxBoneIndex =
                    (uint)max(0, boneCount - 1);

                return min(
                    rawBoneIndex,
                    maxBoneIndex);
            }

            float4 ApplyGpuSkinningPosition(
                float4 positionOS,
                float4 weights,
                uint4 indices,
                float4 animation)
            {
                if (_SkinningEnabled < 0.5)
                {
                    return positionOS;
                }

                int boneCount =
                    max(1, (int)round(_SkinningBoneCount));

                if (boneCount <= 0)
                {
                    return positionOS;
                }

                float weightSum =
                    GetWeightSum(weights);

                if (weightSum <= 0.0001)
                {
                    return positionOS;
                }

                int clipIndex =
                    GetSafeClipIndex(animation.x);

                SkinningClipInfo clip =
                    _SkinningClipInfos[clipIndex];

                int frameCount =
                    max(clip.FrameCount, 1);

                // 기존 SwarmIndirectURP.shader와 동일한 방식.
                // saturate가 아니라 frac를 사용해야 반복 애니메이션이 멈추지 않는다.
                float normalizedTime =
                    frac(animation.y);

                int frame =
                    min(
                        (int)floor(normalizedTime * frameCount),
                        frameCount - 1);

                int matrixBase =
                    (clip.StartFrame + frame) * boneCount;

                uint boneIndex0 =
                    GetSafeBoneIndex(indices.x);

                uint boneIndex1 =
                    GetSafeBoneIndex(indices.y);

                uint boneIndex2 =
                    GetSafeBoneIndex(indices.z);

                uint boneIndex3 =
                    GetSafeBoneIndex(indices.w);

                float4 skinned =
                    float4(0.0, 0.0, 0.0, 0.0);

                skinned +=
                    mul(
                        _SkinningMatrices[matrixBase + boneIndex0],
                        positionOS) * weights.x;

                skinned +=
                    mul(
                        _SkinningMatrices[matrixBase + boneIndex1],
                        positionOS) * weights.y;

                skinned +=
                    mul(
                        _SkinningMatrices[matrixBase + boneIndex2],
                        positionOS) * weights.z;

                skinned +=
                    mul(
                        _SkinningMatrices[matrixBase + boneIndex3],
                        positionOS) * weights.w;

                skinned.w = 1.0;

                return skinned;
            }

            #endif

            // ======================================================
            // Debug Color
            // ======================================================

            float4 GetClipDebugColor(float clipIndex)
            {
                if (clipIndex < 0.5)
                {
                    return float4(0.65, 0.65, 0.65, 1.0);
                }

                if (clipIndex < 1.5)
                {
                    return float4(0.35, 0.75, 1.00, 1.0);
                }

                if (clipIndex < 2.5)
                {
                    return float4(0.35, 1.00, 0.45, 1.0);
                }

                return float4(1.00, 0.35, 0.25, 1.0);
            }

            // ======================================================
            // Vertex Shader
            // ======================================================

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);

                Varyings output;
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float4 positionYaw =
                    GetPositionYaw();

                float4 offsetScale =
                    GetOffsetScale();

                float4 animation =
                    GetAnimation();

                float3 unitRootWorldPos =
                    positionYaw.xyz;

                float facingYaw =
                    positionYaw.w;

                // CurrentLocalPos는 이미 ECS 쪽에서 대열 회전이 반영된
                // Unit root 기준 world-space offset이다.
                // 따라서 offsetScale.xyz는 여기서 다시 yaw 회전하지 않는다.
                float3 soldierWorldOffset =
                    offsetScale.xyz;

                float renderScale =
                    max(offsetScale.w, 0.0001);

                float4 finalPositionOS =
                    input.positionOS;

                #if defined(SWARM_GPU_SKINNING_ON)

                    finalPositionOS =
                        ApplyGpuSkinningPosition(
                            input.positionOS,
                            input.blendWeights,
                            input.blendIndices,
                            animation);

                #endif

                float3 scaledPositionOS =
                    finalPositionOS.xyz * renderScale;

                float3 rotatedPositionWS =
                    RotateY(
                        scaledPositionOS,
                        facingYaw);

                float3 positionWS =
                    unitRootWorldPos +
                    soldierWorldOffset +
                    rotatedPositionWS;

                positionWS.y += _YOffset;

                output.positionCS =
                    TransformWorldToHClip(positionWS);

                output.uv =
                    TRANSFORM_TEX(
                        input.uv,
                        _BaseMap);

                output.animation =
                    animation;

                return output;
            }

            // ======================================================
            // Fragment Shader
            // ======================================================

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 baseMap =
                    SAMPLE_TEXTURE2D(
                        _BaseMap,
                        sampler_BaseMap,
                        input.uv);

                half4 color =
                    baseMap *
                    _BaseColor *
                    GetSwarmFactionColor(input.animation.w);

                if (_DebugClipColor > 0.5)
                {
                    color *= GetClipDebugColor(input.animation.x);
                }

                return color;
            }

            ENDHLSL
        }
    }

    FallBack Off
}
