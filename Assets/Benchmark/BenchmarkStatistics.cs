using System;
using System.Collections.Generic;

namespace Swarm.Benchmark
{
    /// <summary>
    /// 한 지표 배열의 통계 요약.
    /// </summary>
    [Serializable]
    public struct MetricStats
    {
        public double mean;
        public double min;
        public double max;
        public double median;
        public double p95;
        public double p99;
        public double std;

        public static MetricStats From(double[] values)
        {
            var result = new MetricStats();
            if (values == null || values.Length == 0)
                return result;

            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int count = sorted.Length;

            double sum = 0.0;
            for (int i = 0; i < count; i++) sum += sorted[i];
            result.mean = sum / count;
            result.min = sorted[0];
            result.max = sorted[count - 1];
            result.median = BenchmarkStatistics.Percentile(sorted, 50.0);
            result.p95 = BenchmarkStatistics.Percentile(sorted, 95.0);
            result.p99 = BenchmarkStatistics.Percentile(sorted, 99.0);

            double varianceSum = 0.0;
            for (int i = 0; i < count; i++)
            {
                double delta = sorted[i] - result.mean;
                varianceSum += delta * delta;
            }
            result.std = Math.Sqrt(varianceSum / count);
            return result;
        }
    }

    /// <summary>
    /// 씬 × 워크로드(N,L) × 렌더 Case(E1~E4) × 반복 한 번 실행에 대한 집계 결과.
    /// </summary>
    [Serializable]
    public sealed class RunSummary
    {
        public string scenePath;
        public string sceneName;
        public int repeatIndex;
        public string renderCase;

        // 논문 실험 식별자.
        public int totalInstances;   // N
        public int swarmPartition;   // L
        public int soldiersPerSwarm; // N/L
        public string experimentCode; // E1~E4
        public bool runValid;

        public string spawnCaseLabel;
        public int spawnTypeIndex;
        public string spawnTypeId;
        public string spawnTypeLabel;
        public int spawnCount;
        public string spawnComposition;
        public int spawnExpectedUnits;
        public int spawnActualUnits;
        public int spawnMeleeUnits;
        public int spawnArcherUnits;
        public int spawnSoldiers;
        public bool spawnVerificationMatched;

        public int frameCount;
        public double measuredSeconds;

        // 프레임 시간(wall) 기반 성능.
        public double avgFps;
        public double onePercentLowFps;      // 1000 / 최저 1% 프레임 평균 (논문 식2)
        public double pointOnePercentLowFps; // 1000 / 최저 0.1% 프레임 평균

        public MetricStats wall;      // 매 프레임 벽시계 프레임 시간(ms)

        // 단계 지표(1초 주기 표본의 통계).
        public MetricStats cpu;       // FrameTimingManager cpuFrameTime
        public MetricStats gpu;       // gpuFrameTime
        public MetricStats cpuMain;   // 메인 스레드
        public MetricStats cpuRender; // 렌더 스레드

        // 구조·관측 지표(1초 주기 평균).
        public double drawCallsMean;
        public double batchesMean;
        public double trianglesMean;
        public double sysMemMeanMB;
        public double sysMemPeakMB;
        public double gcMemPeakMB;

        // 기존 BRG 전용 요약과의 하위 호환성.
        public int brgInstancesMean;
        public int brgBatchesMean;
        public int brgDrawCmdsMean;

        // 공통 렌더러 구조 계측 요약(1초 주기).
        public bool rendererInstrumentationEnabled;
        public int rendererValidFrameCount;
        public string rendererMeasuredCase;
        public string rendererSubmissionMode;

        public double rendererInstanceCountMean;
        public double rendererActiveSubMeshCountMean;
        public double rendererActiveMaterialCountMean;
        public double rendererCommandsPerSubMeshMean;
        public double rendererSubmittedCommandCountMean;
        public double rendererApiCallCountMean;
        public double rendererUploadedBytesPerFrameMean;

        // RenderMeshIndirect 전용.
        public double rmiComputeDispatchCountMean;

        // BRG 전용.
        public double brgBatchCountMeasuredMean;
        public double brgDrawRangeCountMean;
        public double brgBatchDrawCommandCountMean;

        public bool frameTimingSupported;
        public string timestampUtc;
    }

    public static class BenchmarkStatistics
    {
        /// <summary> 오름차순 정렬 배열의 선형 보간 백분위수. </summary>
        public static double Percentile(double[] sortedAsc, double percentile)
        {
            if (sortedAsc == null || sortedAsc.Length == 0) return 0.0;
            if (sortedAsc.Length == 1) return sortedAsc[0];

            double rank = percentile / 100.0 * (sortedAsc.Length - 1);
            int lower = Math.Max(0, (int)Math.Floor(rank));
            int upper = Math.Min(sortedAsc.Length - 1, (int)Math.Ceiling(rank));
            double fraction = rank - lower;
            return sortedAsc[lower] + (sortedAsc[upper] - sortedAsc[lower]) * fraction;
        }

        /// <summary>
        /// 오름차순 정렬 배열에서 가장 느린(값이 큰) 상위 percent%의 평균.
        /// 논문 식(2)의 1% Low 계산에 사용.
        /// </summary>
        public static double MeanOfSlowest(double[] sortedAsc, double percent)
        {
            if (sortedAsc == null || sortedAsc.Length == 0) return 0.0;
            int k = Math.Max(1, (int)Math.Ceiling(sortedAsc.Length * percent / 100.0));
            k = Math.Min(k, sortedAsc.Length);
            double sum = 0.0;
            for (int i = sortedAsc.Length - k; i < sortedAsc.Length; i++)
                sum += sortedAsc[i];
            return sum / k;
        }

        public static RunSummary Build(
            List<double> frameWallMs,
            List<PeriodicSample> periodicSamples,
            List<SwarmRendererInstrumentationSample> rendererSamples,
            string scenePath,
            int repeatIndex,
            string renderCaseLabel,
            double measuredSeconds,
            bool frameTimingSupported)
        {
            int frameCount = frameWallMs != null ? frameWallMs.Count : 0;

            // ── 프레임 시간(wall) ────────────────────────────────
            var wall = new double[frameCount];
            for (int i = 0; i < frameCount; i++)
                wall[i] = frameWallMs[i];

            var wallSorted = (double[])wall.Clone();
            Array.Sort(wallSorted);
            double slowest1 = MeanOfSlowest(wallSorted, 1.0);
            double slowest01 = MeanOfSlowest(wallSorted, 0.1);

            // ── 1초 주기 단계·구조 표본 ─────────────────────────
            int periodicCount = periodicSamples != null ? periodicSamples.Count : 0;
            var cpu = new double[periodicCount];
            var gpu = new double[periodicCount];
            var main = new double[periodicCount];
            var render = new double[periodicCount];

            double drawSum = 0.0, batchSum = 0.0, triangleSum = 0.0;
            double systemMemorySum = 0.0, systemMemoryMax = 0.0, gcMemoryMax = 0.0;

            for (int i = 0; i < periodicCount; i++)
            {
                PeriodicSample p = periodicSamples[i];
                cpu[i] = p.cpuFrameMs;
                gpu[i] = p.gpuFrameMs;
                main[i] = p.mainThreadMs;
                render[i] = p.renderThreadMs;

                drawSum += p.drawCalls;
                batchSum += p.batches;
                triangleSum += p.triangles;
                systemMemorySum += p.sysMemMB;
                systemMemoryMax = Math.Max(systemMemoryMax, p.sysMemMB);
                gcMemoryMax = Math.Max(gcMemoryMax, p.gcMemMB);
            }

            // ── 렌더러 구조 계측(1초 주기 표본) ─────────────────
            int rendererValidCount = 0;
            long rendererInstanceSum = 0, rendererSubMeshSum = 0, rendererMaterialSum = 0;
            long rendererCommandsPerSubMeshSum = 0, rendererSubmittedCommandSum = 0;
            long rendererApiCallSum = 0, rendererUploadBytesSum = 0, rendererComputeDispatchSum = 0;
            long rendererBatchSum = 0, rendererDrawRangeSum = 0, rendererBatchDrawCommandSum = 0;

            string rendererMeasuredCase = "-";
            string rendererSubmissionMode = "-";

            if (rendererSamples != null)
            {
                for (int i = 0; i < rendererSamples.Count; i++)
                {
                    SwarmRendererInstrumentationSample sample = rendererSamples[i];
                    if (!sample.IsValid) continue;

                    if (rendererValidCount == 0)
                    {
                        rendererMeasuredCase = sample.RenderCase.ToString();
                        rendererSubmissionMode = sample.SubmissionMode.ToString();
                    }

                    rendererValidCount++;
                    rendererInstanceSum += sample.InstanceCount;
                    rendererSubMeshSum += sample.ActiveSubMeshCount;
                    rendererMaterialSum += sample.ActiveMaterialCount;
                    rendererCommandsPerSubMeshSum += sample.CommandsPerSubMesh;
                    rendererSubmittedCommandSum += sample.SubmittedCommandCount;
                    rendererApiCallSum += sample.ApiCallCount;
                    rendererUploadBytesSum += sample.UploadedBytesPerFrame;
                    rendererComputeDispatchSum += sample.ComputeDispatchCount;
                    rendererBatchSum += sample.BatchCount;
                    rendererDrawRangeSum += sample.DrawRangeCount;
                    rendererBatchDrawCommandSum += sample.BatchDrawCommandCount;
                }
            }

            double RendererMean(long sum) =>
                rendererValidCount > 0 ? (double)sum / rendererValidCount : 0.0;

            return new RunSummary
            {
                scenePath = scenePath,
                sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath),
                repeatIndex = repeatIndex,
                renderCase = string.IsNullOrEmpty(renderCaseLabel) ? "-" : renderCaseLabel,

                frameCount = frameCount,
                measuredSeconds = measuredSeconds,

                avgFps = measuredSeconds > 0.0 ? frameCount / measuredSeconds : 0.0,
                onePercentLowFps = slowest1 > 0.0 ? 1000.0 / slowest1 : 0.0,
                pointOnePercentLowFps = slowest01 > 0.0 ? 1000.0 / slowest01 : 0.0,

                wall = MetricStats.From(wall),
                cpu = MetricStats.From(cpu),
                gpu = MetricStats.From(gpu),
                cpuMain = MetricStats.From(main),
                cpuRender = MetricStats.From(render),

                drawCallsMean = periodicCount > 0 ? drawSum / periodicCount : 0.0,
                batchesMean = periodicCount > 0 ? batchSum / periodicCount : 0.0,
                trianglesMean = periodicCount > 0 ? triangleSum / periodicCount : 0.0,
                sysMemMeanMB = periodicCount > 0 ? systemMemorySum / periodicCount : 0.0,
                sysMemPeakMB = systemMemoryMax,
                gcMemPeakMB = gcMemoryMax,

                brgInstancesMean = (int)RendererMean(rendererInstanceSum),
                brgBatchesMean = (int)RendererMean(rendererBatchSum),
                brgDrawCmdsMean = (int)RendererMean(rendererBatchDrawCommandSum),

                rendererInstrumentationEnabled = rendererSamples != null,
                rendererValidFrameCount = rendererValidCount,
                rendererMeasuredCase = rendererMeasuredCase,
                rendererSubmissionMode = rendererSubmissionMode,

                rendererInstanceCountMean = RendererMean(rendererInstanceSum),
                rendererActiveSubMeshCountMean = RendererMean(rendererSubMeshSum),
                rendererActiveMaterialCountMean = RendererMean(rendererMaterialSum),
                rendererCommandsPerSubMeshMean = RendererMean(rendererCommandsPerSubMeshSum),
                rendererSubmittedCommandCountMean = RendererMean(rendererSubmittedCommandSum),
                rendererApiCallCountMean = RendererMean(rendererApiCallSum),
                rendererUploadedBytesPerFrameMean = RendererMean(rendererUploadBytesSum),

                rmiComputeDispatchCountMean = RendererMean(rendererComputeDispatchSum),

                brgBatchCountMeasuredMean = RendererMean(rendererBatchSum),
                brgDrawRangeCountMean = RendererMean(rendererDrawRangeSum),
                brgBatchDrawCommandCountMean = RendererMean(rendererBatchDrawCommandSum),

                frameTimingSupported = frameTimingSupported,
                timestampUtc = DateTime.UtcNow.ToString("o")
            };
        }
    }
}
