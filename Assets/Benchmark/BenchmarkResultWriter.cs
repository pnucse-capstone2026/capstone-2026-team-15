using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Swarm.Benchmark
{
    /// <summary>
    /// 결과를 요약 CSV(summary.csv), 프레임별 CSV, 1초 주기 CSV, 조건별 집계(aggregate.csv),
    /// JSON(report.json)으로 저장한다. 출력 폴더가 고정(빌드 -benchOutput)이면 재개를 지원한다.
    /// </summary>
    public sealed class BenchmarkResultWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private string _folder;
        private string _summaryPath;
        private StreamWriter _summary;
        private StreamWriter _progress;
        private int _ordinal;

        private readonly List<RunSummary> _all = new List<RunSummary>();
        private readonly HashSet<string> _completed = new HashSet<string>();

        public string Folder => _folder;

        private static string F(double value) => value.ToString("0.####", Inv);
        private static string I(long value) => value.ToString(Inv);
        private static string Q(string value) =>
            "\"" + (value ?? string.Empty).Replace("\"", "'") + "\"";

        public static string ExperimentCode(SwarmRenderPipelineCase renderCase) =>
            "E" + SwarmRenderPipelineCaseUtility.GetCaseIndex(renderCase);

        public static string RunKey(
            string sceneName, int totalInstances, int swarmPartition,
            string experimentCode, int repeatIndex) =>
            $"{sceneName}|{totalInstances}|{swarmPartition}|{experimentCode}|{repeatIndex}";

        private const string SummaryHeader =
            "scene,sceneName,repeat,renderCase,experimentCode,N,L,n_swarm,runValid," +
            "spawnCaseLabel,spawnTypeIndex,spawnTypeId,spawnTypeLabel," +
            "spawnCount,spawnComposition,spawnExpectedUnits,spawnActualUnits,spawnMeleeUnits," +
            "spawnArcherUnits,spawnSoldiers,spawnVerificationMatched," +
            "frames,seconds,avgFps,onePctLowFps,p01PctLowFps," +
            "wall_mean_ms,wall_min_ms,wall_max_ms,wall_median_ms,wall_p95_ms,wall_p99_ms,wall_std_ms," +
            "cpu_mean_ms,cpu_max_ms,gpu_mean_ms,gpu_max_ms,cpuMain_mean_ms,cpuRender_mean_ms," +
            "drawCalls_mean,batches_mean,tris_mean,sysMem_mean_mb,sysMem_peak_mb,gc_peak_mb," +
            "brgInstances_mean,brgBatches_mean,brgDrawCmds_mean," +
            "rendererInstrumentationEnabled,rendererValidFrames,rendererMeasuredCase,rendererSubmissionMode," +
            "rendererInstanceCount_mean,rendererActiveSubMeshCount_mean,rendererActiveMaterialCount_mean," +
            "rendererCommandsPerSubMesh_mean,rendererSubmittedCommandCount_mean,rendererApiCallCount_mean," +
            "rendererUploadedBytesPerFrame_mean,rmiComputeDispatchCount_mean," +
            "brgBatchCountMeasured_mean,brgDrawRangeCount_mean,brgBatchDrawCommandCount_mean," +
            "frameTimingSupported,timestampUtc";

        /// <summary> 출력 폴더를 준비한다. resume=true면 기존 summary.csv/진행파일에 이어 쓴다. </summary>
        public void Begin(string folder, bool resume)
        {
            _folder = folder; // 러너가 이미 확정한 세션 폴더(날짜_번호)

            Directory.CreateDirectory(_folder);
            _summaryPath = Path.Combine(_folder, "summary.csv");
            string progressPath = Path.Combine(_folder, "_progress.txt");

            bool append = resume && File.Exists(_summaryPath);
            if (append)
                LoadCompleted(progressPath);

            _summary = new StreamWriter(_summaryPath, append);
            if (!append)
            {
                _summary.WriteLine(SummaryHeader);
                _summary.Flush();
            }

            _progress = new StreamWriter(progressPath, append) { AutoFlush = true };
        }

        private void LoadCompleted(string progressPath)
        {
            if (!File.Exists(progressPath)) return;
            foreach (string line in File.ReadAllLines(progressPath))
            {
                string key = line.Trim();
                if (key.Length > 0) _completed.Add(key);
            }
        }

        public bool IsCompleted(string runKey) => _completed.Contains(runKey);

        public void AppendSummary(RunSummary summary)
        {
            _all.Add(summary);

            var cells = new[]
            {
                Q(summary.scenePath), Q(summary.sceneName), I(summary.repeatIndex), Q(summary.renderCase),
                Q(summary.experimentCode), I(summary.totalInstances), I(summary.swarmPartition),
                I(summary.soldiersPerSwarm), summary.runValid ? "true" : "false",

                Q(summary.spawnCaseLabel), I(summary.spawnTypeIndex), Q(summary.spawnTypeId), Q(summary.spawnTypeLabel),
                I(summary.spawnCount), Q(summary.spawnComposition), I(summary.spawnExpectedUnits),
                I(summary.spawnActualUnits), I(summary.spawnMeleeUnits), I(summary.spawnArcherUnits),
                I(summary.spawnSoldiers), summary.spawnVerificationMatched ? "true" : "false",

                I(summary.frameCount), F(summary.measuredSeconds),
                F(summary.avgFps), F(summary.onePercentLowFps), F(summary.pointOnePercentLowFps),

                F(summary.wall.mean), F(summary.wall.min), F(summary.wall.max), F(summary.wall.median),
                F(summary.wall.p95), F(summary.wall.p99), F(summary.wall.std),

                F(summary.cpu.mean), F(summary.cpu.max), F(summary.gpu.mean), F(summary.gpu.max),
                F(summary.cpuMain.mean), F(summary.cpuRender.mean),

                F(summary.drawCallsMean), F(summary.batchesMean), F(summary.trianglesMean),
                F(summary.sysMemMeanMB), F(summary.sysMemPeakMB), F(summary.gcMemPeakMB),

                I(summary.brgInstancesMean), I(summary.brgBatchesMean), I(summary.brgDrawCmdsMean),

                summary.rendererInstrumentationEnabled ? "true" : "false",
                I(summary.rendererValidFrameCount),
                Q(summary.rendererMeasuredCase), Q(summary.rendererSubmissionMode),

                F(summary.rendererInstanceCountMean), F(summary.rendererActiveSubMeshCountMean),
                F(summary.rendererActiveMaterialCountMean), F(summary.rendererCommandsPerSubMeshMean),
                F(summary.rendererSubmittedCommandCountMean), F(summary.rendererApiCallCountMean),
                F(summary.rendererUploadedBytesPerFrameMean), F(summary.rmiComputeDispatchCountMean),

                F(summary.brgBatchCountMeasuredMean), F(summary.brgDrawRangeCountMean),
                F(summary.brgBatchDrawCommandCountMean),

                summary.frameTimingSupported ? "true" : "false", Q(summary.timestampUtc)
            };

            _summary.WriteLine(string.Join(",", cells));
            _summary.Flush();

            string key = RunKey(
                summary.sceneName, summary.totalInstances, summary.swarmPartition,
                summary.experimentCode, summary.repeatIndex);
            if (_completed.Add(key))
                _progress.WriteLine(key);
        }

        /// <summary> 한 실행의 프레임별 CSV(wall/fps)와 1초 주기 CSV(단계·구조)를 저장. </summary>
        public void WriteRunSamples(
            RunSummary metadata,
            List<double> frameWallMs,
            List<PeriodicSample> periodicSamples,
            List<SwarmRendererInstrumentationSample> rendererSamples,
            bool writePerFrame)
        {
            _ordinal++;
            string prefix = string.Format(
                "run{0:000}_{1}_{2}_N{3}_L{4}_r{5}",
                _ordinal, Sanitize(metadata.sceneName), Sanitize(metadata.renderCase),
                metadata.totalInstances, metadata.swarmPartition, metadata.repeatIndex);

            if (writePerFrame && frameWallMs != null)
            {
                using var w = new StreamWriter(Path.Combine(_folder, prefix + "_frame.csv"), false);
                w.WriteLine("frame,wall_ms,fps");
                for (int i = 0; i < frameWallMs.Count; i++)
                {
                    double wall = frameWallMs[i];
                    double fps = wall > 0.0 ? 1000.0 / wall : 0.0;
                    w.WriteLine($"{I(i)},{F(wall)},{F(fps)}");
                }
            }

            WritePerSecond(Path.Combine(_folder, prefix + "_persec.csv"), periodicSamples, rendererSamples);
        }

        private static void WritePerSecond(
            string file,
            List<PeriodicSample> periodicSamples,
            List<SwarmRendererInstrumentationSample> rendererSamples)
        {
            using var w = new StreamWriter(file, false);
            w.WriteLine(
                "sec,cpu_ms,gpu_ms,main_ms,render_ms,presentWait_ms," +
                "drawCalls,setPass,batches,triangles,sysMem_mb,gc_mb," +
                "renderer_valid,renderer_case,renderer_submission_mode," +
                "instance_count,active_submesh,active_material,commands_per_submesh," +
                "submitted_command_count,api_call_count,uploaded_bytes," +
                "rmi_compute_dispatch,brg_batch_count,brg_draw_range,brg_batch_draw_command");

            int count = periodicSamples != null ? periodicSamples.Count : 0;
            for (int i = 0; i < count; i++)
            {
                PeriodicSample p = periodicSamples[i];

                bool hasRenderer = rendererSamples != null && i < rendererSamples.Count && rendererSamples[i].IsValid;
                SwarmRendererInstrumentationSample r = hasRenderer ? rendererSamples[i] : default;

                var cells = new[]
                {
                    I(i), F(p.cpuFrameMs), F(p.gpuFrameMs), F(p.mainThreadMs), F(p.renderThreadMs), F(p.presentWaitMs),
                    I(p.drawCalls), I(p.setPassCalls), I(p.batches), I(p.triangles), F(p.sysMemMB), F(p.gcMemMB),

                    hasRenderer ? "1" : "0",
                    Q(hasRenderer ? r.RenderCase.ToString() : string.Empty),
                    Q(hasRenderer ? r.SubmissionMode.ToString() : string.Empty),
                    I(hasRenderer ? r.InstanceCount : 0),
                    I(hasRenderer ? r.ActiveSubMeshCount : 0),
                    I(hasRenderer ? r.ActiveMaterialCount : 0),
                    I(hasRenderer ? r.CommandsPerSubMesh : 0),
                    I(hasRenderer ? r.SubmittedCommandCount : 0),
                    I(hasRenderer ? r.ApiCallCount : 0),
                    I(hasRenderer ? r.UploadedBytesPerFrame : 0),
                    I(hasRenderer ? r.ComputeDispatchCount : 0),
                    I(hasRenderer ? r.BatchCount : 0),
                    I(hasRenderer ? r.DrawRangeCount : 0),
                    I(hasRenderer ? r.BatchDrawCommandCount : 0)
                };
                w.WriteLine(string.Join(",", cells));
            }
        }

        public void Finish(BenchmarkConfig config)
        {
            if (_summary != null)
            {
                _summary.Flush();
                _summary.Dispose();
                _summary = null;
            }
            if (_progress != null)
            {
                _progress.Dispose();
                _progress = null;
            }

            BuildAggregate();

            var report = new BenchmarkReport
            {
                generatedUtc = DateTime.UtcNow.ToString("o"),
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                gpuName = SystemInfo.graphicsDeviceName,
                gpuApi = SystemInfo.graphicsDeviceType.ToString(),
                cpuName = SystemInfo.processorType,
                cpuCount = SystemInfo.processorCount,
                systemMemoryMB = SystemInfo.systemMemorySize,
                durationSeconds = config != null ? config.durationSeconds : 0f,
                repeats = config != null ? config.repeats : 0,
                warmupSeconds = config != null ? config.warmupSeconds : 0f,
                sweepRenderCases = config != null && config.sweepRenderCases,
                rendererInstrumentationEnabled = config != null && config.enableRendererInstrumentation,
                rangedProjectileRenderingEnabled = config == null || config.renderRangedProjectiles,
                runs = _all
            };

            File.WriteAllText(
                Path.Combine(_folder, "report.json"),
                JsonUtility.ToJson(report, true));

            // 완료 마커: 재개 로직이 "완료된 세션"을 새 폴더로 넘기는 근거
            File.WriteAllText(
                Path.Combine(_folder, "_complete.marker"),
                DateTime.UtcNow.ToString("o"));
        }

        /// <summary>
        /// summary.csv(누적)를 읽어 (scene,N,L,experimentCode)별로 유효 실행의
        /// 평균/표준편차를 aggregate.csv 로 쓴다. (논문 "5회 평균±표준편차")
        /// </summary>
        private void BuildAggregate()
        {
            if (!File.Exists(_summaryPath)) return;
            string[] lines = File.ReadAllLines(_summaryPath);
            if (lines.Length < 2) return;

            string[] header = lines[0].Split(',');
            var idx = new Dictionary<string, int>();
            for (int i = 0; i < header.Length; i++) idx[header[i]] = i;

            string[] metrics =
            {
                "avgFps", "onePctLowFps", "wall_mean_ms", "wall_p95_ms", "wall_p99_ms",
                "gpu_mean_ms", "cpuMain_mean_ms", "cpuRender_mean_ms",
                "rendererSubmittedCommandCount_mean", "rendererUploadedBytesPerFrame_mean"
            };
            foreach (string m in metrics)
                if (!idx.ContainsKey(m)) return; // 헤더 불일치 → 스킵

            var order = new List<string>();
            var groups = new Dictionary<string, List<double[]>>();
            var meta = new Dictionary<string, string[]>();

            for (int r = 1; r < lines.Length; r++)
            {
                string[] c = lines[r].Split(',');
                if (c.Length < header.Length) continue;
                if (Get(c, idx, "runValid") != "true") continue;

                string scene = Strip(Get(c, idx, "sceneName"));
                string n = Get(c, idx, "N");
                string l = Get(c, idx, "L");
                string ecode = Strip(Get(c, idx, "experimentCode"));
                string key = scene + "|" + n + "|" + l + "|" + ecode;

                var vals = new double[metrics.Length];
                for (int m = 0; m < metrics.Length; m++)
                    double.TryParse(Get(c, idx, metrics[m]), NumberStyles.Float, Inv, out vals[m]);

                if (!groups.TryGetValue(key, out List<double[]> list))
                {
                    list = new List<double[]>();
                    groups[key] = list;
                    meta[key] = new[] { scene, n, l, ecode };
                    order.Add(key);
                }
                list.Add(vals);
            }

            using var w = new StreamWriter(Path.Combine(_folder, "aggregate.csv"), false);
            var head = new List<string> { "scene", "N", "L", "experimentCode", "runs" };
            foreach (string m in metrics) { head.Add(m + "_mean"); head.Add(m + "_std"); }
            w.WriteLine(string.Join(",", head));

            foreach (string key in order)
            {
                List<double[]> rows = groups[key];
                string[] m4 = meta[key];
                var row = new List<string> { Q(m4[0]), m4[1], m4[2], Q(m4[3]), I(rows.Count) };

                for (int m = 0; m < metrics.Length; m++)
                {
                    double mean = 0.0;
                    foreach (double[] v in rows) mean += v[m];
                    mean /= rows.Count;
                    double variance = 0.0;
                    foreach (double[] v in rows) { double d = v[m] - mean; variance += d * d; }
                    double std = Math.Sqrt(variance / rows.Count);
                    row.Add(F(mean));
                    row.Add(F(std));
                }
                w.WriteLine(string.Join(",", row));
            }
        }

        private static string Get(string[] cells, Dictionary<string, int> idx, string name) =>
            idx.TryGetValue(name, out int i) && i < cells.Length ? cells[i] : string.Empty;

        private static string Strip(string s)
        {
            if (s != null && s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "none";
            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; i++)
                if (!char.IsLetterOrDigit(characters[i])) characters[i] = '_';
            return new string(characters);
        }
    }

    [Serializable]
    public sealed class BenchmarkReport
    {
        public string generatedUtc;
        public string unityVersion;
        public string platform;
        public string gpuName;
        public string gpuApi;
        public string cpuName;
        public int cpuCount;
        public int systemMemoryMB;

        public float durationSeconds;
        public int repeats;
        public float warmupSeconds;
        public bool sweepRenderCases;
        public bool rendererInstrumentationEnabled;
        public bool rangedProjectileRenderingEnabled;

        public List<RunSummary> runs;
    }
}
