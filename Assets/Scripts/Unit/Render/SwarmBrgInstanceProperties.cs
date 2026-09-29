using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Swarm
{
    public readonly struct SwarmBrgInstanceBufferLayout
    {
        public readonly int InstanceCapacity;

        public readonly int PositionYawOffsetBytes;
        public readonly int OffsetScaleOffsetBytes;
        public readonly int AnimationOffsetBytes;

        public readonly int PositionYawCount;
        public readonly int OffsetScaleCount;
        public readonly int AnimationCount;

        public readonly int TotalFloat4Count;
        public readonly int TotalSizeBytes;

        // GraphicsBuffer.Target.Raw 사용 시 count로 사용할 값.
        // Raw buffer는 보통 sizeof(int) stride 기준으로 만든다.
        public readonly int TotalRawIntCount;

        public SwarmBrgInstanceBufferLayout(
            int instanceCapacity,
            int positionYawOffsetBytes,
            int offsetScaleOffsetBytes,
            int animationOffsetBytes,
            int positionYawCount,
            int offsetScaleCount,
            int animationCount,
            int totalFloat4Count,
            int totalSizeBytes,
            int totalRawIntCount)
        {
            InstanceCapacity = instanceCapacity;

            PositionYawOffsetBytes = positionYawOffsetBytes;
            OffsetScaleOffsetBytes = offsetScaleOffsetBytes;
            AnimationOffsetBytes = animationOffsetBytes;

            PositionYawCount = positionYawCount;
            OffsetScaleCount = offsetScaleCount;
            AnimationCount = animationCount;

            TotalFloat4Count = totalFloat4Count;
            TotalSizeBytes = totalSizeBytes;
            TotalRawIntCount = totalRawIntCount;
        }
    }

    public static class SwarmBrgInstanceProperties
    {
        public const int Float4SizeBytes = 16;
        public const int RawIntSizeBytes = 4;

        // 논리적 packed size.
        // 실제 메모리 배치는 SoA지만, instance 하나는 의미상 float4 3개를 가진다.
        public const int LogicalPackedFloat4CountPerInstance = 3;
        public const int LogicalPackedBytesPerInstance =
            Float4SizeBytes * LogicalPackedFloat4CountPerInstance;

        // DOTS Instancing metadata high bit.
        // 이 bit가 켜지면 shader 측에서 per-instance array로 접근한다.
        public const uint IsPerInstanceArray = 0x80000000u;

        // address 0을 실제 데이터 시작점으로 쓰지 않기 위한 예비 영역.
        // metadata가 0이 되는 누락 property와 실제 데이터 시작점을 분리한다.
        public const int ReservedBytes = 64;

        public static readonly int PositionYawID = Shader.PropertyToID("PositionYaw");
        public static readonly int OffsetScaleID = Shader.PropertyToID("OffsetScale");
        public static readonly int AnimationID = Shader.PropertyToID("Animation");

        public static SwarmBrgInstanceBufferLayout CalculateLayout(
            int requestedInstanceCapacity)
        {
            int instanceCapacity =
                math.max(1, requestedInstanceCapacity);

            int reservedFloat4Count =
                math.max(1, ReservedBytes / Float4SizeBytes);

            int positionYawStartFloat4 =
                reservedFloat4Count;

            int offsetScaleStartFloat4 =
                positionYawStartFloat4 + instanceCapacity;

            int animationStartFloat4 =
                offsetScaleStartFloat4 + instanceCapacity;

            int totalFloat4Count =
                animationStartFloat4 + instanceCapacity;

            int totalSizeBytes =
                totalFloat4Count * Float4SizeBytes;

            int totalRawIntCount =
                totalSizeBytes / RawIntSizeBytes;

            return new SwarmBrgInstanceBufferLayout(
                instanceCapacity: instanceCapacity,
                positionYawOffsetBytes: positionYawStartFloat4 * Float4SizeBytes,
                offsetScaleOffsetBytes: offsetScaleStartFloat4 * Float4SizeBytes,
                animationOffsetBytes: animationStartFloat4 * Float4SizeBytes,
                positionYawCount: instanceCapacity,
                offsetScaleCount: instanceCapacity,
                animationCount: instanceCapacity,
                totalFloat4Count: totalFloat4Count,
                totalSizeBytes: totalSizeBytes,
                totalRawIntCount: totalRawIntCount
            );
        }

        public static MetadataValue[] CreateMetadata(
            in SwarmBrgInstanceBufferLayout layout)
        {
            return new[]
            {
                new MetadataValue
                {
                    NameID = PositionYawID,
                    Value = IsPerInstanceArray | (uint)layout.PositionYawOffsetBytes
                },
                new MetadataValue
                {
                    NameID = OffsetScaleID,
                    Value = IsPerInstanceArray | (uint)layout.OffsetScaleOffsetBytes
                },
                new MetadataValue
                {
                    NameID = AnimationID,
                    Value = IsPerInstanceArray | (uint)layout.AnimationOffsetBytes
                }
            };
        }

        public static int GetPositionYawStartIndex(
            in SwarmBrgInstanceBufferLayout layout)
        {
            return layout.PositionYawOffsetBytes / Float4SizeBytes;
        }

        public static int GetOffsetScaleStartIndex(
            in SwarmBrgInstanceBufferLayout layout)
        {
            return layout.OffsetScaleOffsetBytes / Float4SizeBytes;
        }

        public static int GetAnimationStartIndex(
            in SwarmBrgInstanceBufferLayout layout)
        {
            return layout.AnimationOffsetBytes / Float4SizeBytes;
        }
    }
}