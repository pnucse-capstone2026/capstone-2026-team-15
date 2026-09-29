Shader "Custom/SwarmIndirectMesh"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        StructuredBuffer<float4x4> _VisibleMatrices;
        StructuredBuffer<float4> _InstanceColors;
        StructuredBuffer<float4x4> _SkinningMatrices;

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

        StructuredBuffer<SoldierAnimationPayload> _SoldierAnimationPayloads;
        StructuredBuffer<SkinningClipInfo> _SkinningClipInfos;

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            int _SkinningEnabled;
            int _SkinningBoneCount;
        CBUFFER_END

        float4 SkinPosition(float4 positionOS, float4 weights, uint4 indices, uint instanceID)
        {
            if (_SkinningEnabled == 0 || _SkinningBoneCount <= 0)
            {
                return positionOS;
            }

            SoldierAnimationPayload payload = _SoldierAnimationPayloads[instanceID];
            SkinningClipInfo clip = _SkinningClipInfos[max(payload.ClipIndex, 0)];
            int frameCount = max(clip.FrameCount, 1);
            float normalizedTime = frac(payload.NormalizedTime);
            int frame = min((int)floor(normalizedTime * frameCount), frameCount - 1);
            int matrixBase = (clip.StartFrame + frame) * _SkinningBoneCount;

            float4 skinned = 0;
            skinned += mul(_SkinningMatrices[matrixBase + indices.x], positionOS) * weights.x;
            skinned += mul(_SkinningMatrices[matrixBase + indices.y], positionOS) * weights.y;
            skinned += mul(_SkinningMatrices[matrixBase + indices.z], positionOS) * weights.z;
            skinned += mul(_SkinningMatrices[matrixBase + indices.w], positionOS) * weights.w;
            skinned.w = 1;
            return skinned;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 blendWeights : BLENDWEIGHTS;
                uint4 blendIndices : BLENDINDICES;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float4 skinnedOS = SkinPosition(v.positionOS, v.blendWeights, v.blendIndices, v.instanceID);
                float4 positionWS = mul(_VisibleMatrices[v.instanceID], skinnedOS);
                o.positionCS = TransformWorldToHClip(positionWS.xyz);
                o.uv = v.uv;
                o.color = _InstanceColors[v.instanceID];
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 baseColor =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor;
                return baseColor * i.color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 blendWeights : BLENDWEIGHTS;
                uint4 blendIndices : BLENDINDICES;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float4 skinnedOS = SkinPosition(v.positionOS, v.blendWeights, v.blendIndices, v.instanceID);
                float4 positionWS = mul(_VisibleMatrices[v.instanceID], skinnedOS);
                o.positionCS = TransformWorldToHClip(positionWS.xyz);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
