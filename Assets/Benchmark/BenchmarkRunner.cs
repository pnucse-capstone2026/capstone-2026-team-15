using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Swarm.Benchmark
{
    /// <summary>
    /// 씬을 워크로드(N,L) × 렌더 Case(E1~E4) × 반복으로 순회 측정한다(논문 실험 설계).
    ///
    /// 프레임 시간(wall)은 매 프레임, 단계·구조 지표와 렌더러 계측은 1초 주기로 수집한다.
    /// 매 실행마다 씬을 새로 로드하고, 스폰 매니저가 N·L을 적용한다.
    /// 출력 폴더가 고정이면(-benchOutput) 완료된 조건은 건너뛰고 이어서 측정한다(재개).
    /// </summary>
    public sealed class BenchmarkRunner : MonoBehaviour
    {
        public BenchmarkConfig Config;
        public bool StopEditorPlayOnComplete = true;
        public bool QuitPlayerOnComplete = true;

        private readonly List<double> _frameWallMs = new List<double>(32768);
        private readonly List<PeriodicSample> _periodicSamples = new List<PeriodicSample>(256);
        private readonly List<SwarmRendererInstrumentationSample> _rendererSamples =
            new List<SwarmRendererInstrumentationSample>(256);

        private readonly BenchmarkResultWriter _writer = new BenchmarkResultWriter();
        private readonly SwarmRendererInstrumentationReader _rendererInstrumentation =
            new SwarmRendererInstrumentationReader();

        private int _origVSync;
        private int _origTarget;
        private bool _origRunInBackground;
        private bool _origProjectileRendering;
        private bool _runtimeSettingsApplied;
        private bool _lastRunValid;

        private const double SpawnApplyTimeoutSeconds = 10d;

        public static BenchmarkRunner Create(BenchmarkConfig config)
        {
            var go = new GameObject("[BenchmarkRunner]");
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<BenchmarkRunner>();
            runner.Config = config;
            return runner;
        }

        private void Start()
        {
            if (Config == null)
            {
                Debug.LogError("[Benchmark] Config 가 없습니다.");
                Destroy(gameObject);
                return;
            }
            StartCoroutine(RunAll());
        }

        private IEnumerator RunAll()
        {
            _origVSync = QualitySettings.vSyncCount;
            _origTarget = Application.targetFrameRate;
            _origRunInBackground = Application.runInBackground;
            _origProjectileRendering = RangedProjectileVisualSystem.RenderingEnabled;
            _runtimeSettingsApplied = true;

            RangedProjectileVisualSystem.RenderingEnabled = Config.renderRangedProjectiles;
            Application.runInBackground = true;
            if (Config.disableVSync) QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = Config.targetFrameRate;

            string baseFolder = string.IsNullOrEmpty(Config.outputFolderOverride)
                ? System.IO.Path.Combine(Application.persistentDataPath, "BenchmarkResults")
                : Config.outputFolderOverride;
            (string sessionFolder, bool resume) = ResolveSessionFolder(baseFolder);
            _writer.Begin(sessionFolder, resume);
            Debug.Log(
                $"[Benchmark] 출력 폴더: {_writer.Folder}{(resume ? " (재개)" : "")} | " +
                $"워크로드: {(Config.HasWorkloadGrid ? Config.WorkloadCount + "개(N×L)" : "off")} | " +
                $"투사체 렌더링: {(Config.renderRangedProjectiles ? "켜짐" : "꺼짐")}");

            // 렌더 케이스 목록 (워크로드 모드에선 renderCases가 곧 E1~E4)
            var cases = new List<SwarmRenderPipelineCase?>();
            bool useCaseList =
                (Config.HasWorkloadGrid || Config.sweepRenderCases) &&
                Config.renderCases != null && Config.renderCases.Length > 0;
            if (useCaseList)
                foreach (SwarmRenderPipelineCase c in Config.renderCases) cases.Add(c);
            else
                cases.Add(null);

            List<BenchmarkWorkload> workloads =
                Config.HasWorkloadGrid ? Config.EnumerateWorkloads() : null;

            string[] scenePaths = Config.scenePaths ?? Array.Empty<string>();
            foreach (string scenePath in scenePaths)
            {
                if (string.IsNullOrEmpty(scenePath)) continue;

                bool loadable =
                    Application.CanStreamedLevelBeLoaded(scenePath) ||
                    Application.CanStreamedLevelBeLoaded(
                        System.IO.Path.GetFileNameWithoutExtension(scenePath));
                if (!loadable)
                {
                    Debug.LogError(
                        $"[Benchmark] 씬을 로드할 수 없습니다(Build Settings 에 추가 필요): {scenePath}");
                    continue;
                }

                int repeats = Mathf.Max(1, Config.repeats);

                if (workloads != null)
                {
                    foreach (BenchmarkWorkload workload in workloads)
                        foreach (SwarmRenderPipelineCase? renderCase in cases)
                            for (int rep = 1; rep <= repeats; rep++)
                            {
                                yield return RunCondition(scenePath, workload, 0, renderCase, rep);
                                yield return WaitRealtime(Config.betweenRunsSeconds);
                            }
                }
                else
                {
                    int spawnCaseCount = Config.HasSpawnAutomation ? Config.SpawnCaseCount : 1;
                    for (int spawnCaseIndex = 0; spawnCaseIndex < spawnCaseCount; spawnCaseIndex++)
                        foreach (SwarmRenderPipelineCase? renderCase in cases)
                            for (int rep = 1; rep <= repeats; rep++)
                            {
                                yield return RunCondition(scenePath, null, spawnCaseIndex, renderCase, rep);
                                yield return WaitRealtime(Config.betweenRunsSeconds);
                            }
                }
            }

            _rendererInstrumentation.SetInstrumentationEnabled(false);
            _writer.Finish(Config);
            Debug.Log($"[Benchmark] 완료 ✅  결과: {_writer.Folder}");

            QualitySettings.vSyncCount = _origVSync;
            Application.targetFrameRate = _origTarget;
            Application.runInBackground = _origRunInBackground;
            RestoreRuntimeSettings();

#if UNITY_EDITOR
            if (StopEditorPlayOnComplete) UnityEditor.EditorApplication.isPlaying = false;
#else
            if (QuitPlayerOnComplete) Application.Quit();
#endif
        }

        private void OnDestroy() => RestoreRuntimeSettings();

        private void RestoreRuntimeSettings()
        {
            if (!_runtimeSettingsApplied) return;
            RangedProjectileVisualSystem.RenderingEnabled = _origProjectileRendering;
            _runtimeSettingsApplied = false;
        }

        // ── 한 조건: 재개 skip + 유효성 재측정 ───────────────────────
        private IEnumerator RunCondition(
            string scenePath, BenchmarkWorkload? workload, int spawnCaseIndex,
            SwarmRenderPipelineCase? renderCase, int rep)
        {
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(scenePath);
            int n = workload?.totalInstances ?? -1;
            int l = workload?.swarmPartition ?? -1;
            string ecode = renderCase.HasValue
                ? BenchmarkResultWriter.ExperimentCode(renderCase.Value)
                : "-";
            string key = BenchmarkResultWriter.RunKey(sceneName, n, l, ecode, rep);

            if (_writer.IsCompleted(key))
            {
                Debug.Log($"[Benchmark] skip(완료됨): {key}");
                yield break;
            }

            int maxAttempts = 1 + Mathf.Max(0, Config.validationRetries);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                yield return RunOnce(scenePath, workload, spawnCaseIndex, renderCase, rep, ecode);
                if (_lastRunValid) yield break;

                Debug.LogWarning(
                    $"[Benchmark] 조건 무효 {key} (시도 {attempt}/{maxAttempts}) → 재측정");
                yield return WaitRealtime(Config.betweenRunsSeconds);
            }

            WriteInvalidStub(scenePath, sceneName, n, l, ecode, renderCase, rep);
        }

        // ── 한 실행: 로드 → 스폰 → 검증 → 측정 → 기록 ────────────────
        private IEnumerator RunOnce(
            string scenePath, BenchmarkWorkload? workload, int spawnCaseIndex,
            SwarmRenderPipelineCase? requestedCase, int repeatIndex, string ecode)
        {
            _lastRunValid = false;

            BenchmarkSpawnAutomation spawnAutomation = workload.HasValue
                ? new BenchmarkSpawnAutomation(Config, workload.Value)
                : new BenchmarkSpawnAutomation(Config, spawnCaseIndex);

            string requestedLabel = requestedCase.HasValue
                ? SwarmRenderPipelineCaseUtility.GetCaseLabel(requestedCase.Value)
                : "-";

            Debug.Log(
                $"[Benchmark] 로드 '{scenePath}' | spawn={spawnAutomation.Label} | " +
                $"{ecode}({requestedLabel}) | run {repeatIndex}/{Config.repeats}");

            AsyncOperation load = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Single);
            if (load == null)
            {
                Debug.LogError($"[Benchmark] 씬 로드 실패: {scenePath}");
                yield break;
            }
            while (!load.isDone) yield return null;
            yield return null; // Awake/OnEnable 및 ECS World 정착

            // 스폰 적용 (LegionBase 등장까지 최대 10초 재시도)
            if (spawnAutomation.IsActive)
            {
                bool applied = false;
                double deadline = Time.realtimeSinceStartupAsDouble + SpawnApplyTimeoutSeconds;
                while (!applied && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    applied = spawnAutomation.TryApply(out int baseCount);
                    if (applied) yield return spawnAutomation.WaitForResult(baseCount);
                    else yield return null;
                }

                if (!applied)
                {
                    Debug.LogWarning(
                        $"[Benchmark] 스폰 적용할 LegionBase를 찾지 못함: {spawnAutomation.Label}");
                    yield break; // 무효
                }
                if (!spawnAutomation.Metadata.VerificationMatched)
                {
                    Debug.LogWarning(
                        $"[Benchmark] 스폰 검증 불일치: {spawnAutomation.Label}");
                    yield break; // 무효 → 재측정
                }
            }

            // 렌더러 Case 구성 + 실험 통제 검증
            if (!_rendererInstrumentation.ConfigureForRun(
                    requestedCase, Config.enableRendererInstrumentation))
            {
                Debug.LogError("[Benchmark] 렌더러 Case 구성/통제 검증 실패 → 재측정");
                yield break; // 무효
            }
            yield return null; // 컴포넌트 활성/케이스 전환 1프레임 반영

            SwarmRenderPipelineCase? resolvedCase = requestedCase;
            if (!resolvedCase.HasValue &&
                _rendererInstrumentation.TryResolveActiveCase(out SwarmRenderPipelineCase active))
                resolvedCase = active;

            string label = resolvedCase.HasValue
                ? SwarmRenderPipelineCaseUtility.GetCaseLabel(resolvedCase.Value)
                : "-";

            // 워밍업 (측정 제외)
            yield return WaitRealtime(Config.warmupSeconds);

            // 측정: wall=매 프레임, 단계·구조=1초 주기
            _frameWallMs.Clear();
            _periodicSamples.Clear();
            _rendererSamples.Clear();

            var sampler = new FrameSampler();
            sampler.Begin();

            double start = Time.realtimeSinceStartupAsDouble;
            double duration = Mathf.Max(1f, Config.durationSeconds);
            double interval = Mathf.Max(0.05f, Config.periodicSampleIntervalSeconds);
            double nextTick = start + interval;

            while (Time.realtimeSinceStartupAsDouble - start < duration)
            {
                yield return null;
                _frameWallMs.Add(sampler.SampleFrameWallMs());

                if (Time.realtimeSinceStartupAsDouble >= nextTick)
                {
                    _periodicSamples.Add(sampler.SamplePeriodic());

                    if (Config.enableRendererInstrumentation)
                    {
                        if (_rendererInstrumentation.TryCapture(
                                resolvedCase, out SwarmRendererInstrumentationSample rs))
                        {
                            _rendererSamples.Add(rs);
                        }
                        else
                        {
                            var invalid = default(SwarmRendererInstrumentationSample);
                            invalid.FrameIndex = Time.frameCount;
                            _rendererSamples.Add(invalid);
                        }
                    }
                    nextTick += interval;
                }
            }

            double measured = Time.realtimeSinceStartupAsDouble - start;
            bool frameTimingSupported = sampler.FrameTimingSupported;
            sampler.Dispose();

            List<SwarmRendererInstrumentationSample> rendererForOut =
                Config.enableRendererInstrumentation ? _rendererSamples : null;

            RunSummary summary = BenchmarkStatistics.Build(
                _frameWallMs, _periodicSamples, rendererForOut,
                scenePath, repeatIndex, label, measured, frameTimingSupported);

            summary.experimentCode = ecode;
            summary.runValid = true;
            spawnAutomation.ApplyMetadata(summary); // N/L/n_swarm/spawn* 기록

            _writer.WriteRunSamples(
                summary, _frameWallMs, _periodicSamples, rendererForOut, Config.writePerFrameCsv);
            _writer.AppendSummary(summary);
            _lastRunValid = true;

            Debug.Log(
                $"[Benchmark] {summary.sceneName} {ecode} N{summary.totalInstances} L{summary.swarmPartition} " +
                $"run{repeatIndex} avgFps={summary.avgFps:0.0} 1%low={summary.onePercentLowFps:0.0} " +
                $"gpu={summary.gpu.mean:0.00}ms cmds={summary.rendererSubmittedCommandCountMean:0.0} " +
                $"frames={summary.frameCount} periodic={_periodicSamples.Count} ftEnabled={frameTimingSupported}");
        }

        private void WriteInvalidStub(
            string scenePath, string sceneName, int n, int l, string ecode,
            SwarmRenderPipelineCase? renderCase, int rep)
        {
            var summary = new RunSummary
            {
                scenePath = scenePath,
                sceneName = sceneName,
                repeatIndex = rep,
                renderCase = renderCase.HasValue
                    ? SwarmRenderPipelineCaseUtility.GetCaseLabel(renderCase.Value)
                    : "-",
                experimentCode = ecode,
                totalInstances = n,
                swarmPartition = l,
                soldiersPerSwarm = l > 0 ? n / l : 0,
                runValid = false,
                rendererMeasuredCase = "-",
                rendererSubmissionMode = "-",
                timestampUtc = DateTime.UtcNow.ToString("o")
            };
            _writer.AppendSummary(summary);
            Debug.LogError(
                $"[Benchmark] 조건 실패(무효 기록): {sceneName} N{n} L{l} {ecode} r{rep}");
        }

        // 출력 경로 아래에 "yyyyMMdd_NN" 세션 폴더를 만든다.
        // 최신 폴더가 완료 마커 없이 남아 있으면(중단됨) 그 폴더를 이어서 쓴다.
        private static (string folder, bool resume) ResolveSessionFolder(string baseFolder)
        {
            System.IO.Directory.CreateDirectory(baseFolder);
            string[] dirs = System.IO.Directory.GetDirectories(baseFolder);

            string latest = null;
            foreach (string d in dirs)
            {
                string name = System.IO.Path.GetFileName(d);
                if (!IsSessionFolder(name)) continue;
                if (latest == null ||
                    string.CompareOrdinal(name, System.IO.Path.GetFileName(latest)) > 0)
                    latest = d;
            }

            if (latest != null &&
                !System.IO.File.Exists(System.IO.Path.Combine(latest, "_complete.marker")))
                return (latest, true);

            string today = DateTime.Now.ToString("yyyyMMdd");
            int maxNn = 0;
            foreach (string d in dirs)
            {
                string name = System.IO.Path.GetFileName(d);
                if (name.Length > today.Length + 1 &&
                    name.StartsWith(today + "_", StringComparison.Ordinal) &&
                    int.TryParse(name.Substring(today.Length + 1), out int nn))
                    maxNn = Math.Max(maxNn, nn);
            }
            string folder = System.IO.Path.Combine(baseFolder, $"{today}_{(maxNn + 1):00}");
            return (folder, false);
        }

        private static bool IsSessionFolder(string name)
        {
            if (name == null || name.Length < 10 || name[8] != '_') return false;
            for (int i = 0; i < 8; i++) if (!char.IsDigit(name[i])) return false;
            for (int i = 9; i < name.Length; i++) if (!char.IsDigit(name[i])) return false;
            return true;
        }

        private static IEnumerator WaitRealtime(float seconds)
        {
            if (seconds <= 0f) yield break;
            double start = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - start < seconds) yield return null;
        }
    }
}
