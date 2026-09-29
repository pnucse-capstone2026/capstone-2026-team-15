using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Swarm.Benchmark.EditorTools
{
    /// <summary>
    /// 개발용 Standalone 빌드를 만든 뒤 "-runBenchmark" 인자로 실행한다.
    /// (하드웨어 지표, 특히 gpuFrameTime 은 빌드에서 가장 정확)
    /// </summary>
    public static class BenchmarkBuilder
    {
        public static void BuildAndRun(BenchmarkConfig cfg)
        {
            if (cfg == null) return;
            if (cfg.scenePaths == null || cfg.scenePaths.Length == 0)
            {
                EditorUtility.DisplayDialog("Swarm Benchmark", "측정할 씬을 1개 이상 추가하세요.", "확인");
                return;
            }

            string dir = EditorUtility.SaveFolderPanel("빌드 출력 폴더 선택", "", "BenchmarkBuild");
            if (string.IsNullOrEmpty(dir)) return;

            PlayerSettings.enableFrameTimingStats = true; // 릴리스/개발 빌드에서 FrameTimingManager 활성화
            PlayerSettings.runInBackground = true;        // 포커스 없이 실행돼도 멈추지 않게(자동 벤치마크 필수)

            // 창모드 체크박스가 곧 빌드 모드다. 체크 → 창모드, 해제 → 전체화면.
            // 매 빌드마다 명시적으로 세팅해, 이전 창모드 빌드의 잔재가 남지 않게 한다.
            if (cfg.windowedBuild)
                ApplyWindowedPlayerSettings(cfg);
            else
                ApplyFullscreenPlayerSettings();

            string[] scenes = BuildSceneList(cfg);
            string exe = Path.Combine(dir, "SwarmBenchmark.exe");

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development // 개발 빌드: 프로파일러 카운터/프레임타이밍 확실히 사용 가능
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[Benchmark] 빌드 실패: {report.summary.result}");
                return;
            }

            string results = Path.Combine(dir, "Results");
            Directory.CreateDirectory(results);

            string args =
                $"-runBenchmark -benchOutput \"{results}\" " +
                $"-benchDuration {cfg.durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"-benchRepeats {cfg.repeats} " +
                $"-benchWarmup {cfg.warmupSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                BuildScreenArgs(cfg) +
                BuildSpawnOverrideArgs(cfg);

            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = dir };
            Process.Start(psi);

            Debug.Log(
                $"[Benchmark] 빌드 완료 → 실행 시작{(cfg.windowedBuild ? $" (창모드 {Mathf.Max(320, cfg.windowedWidth)}x{Mathf.Max(240, cfg.windowedHeight)})" : "")}. " +
                $"결과 폴더: {results}");
            EditorUtility.RevealInFinder(results);
        }

        // ── 창모드 / 전체화면 빌드 설정 (체크박스가 곧 빌드 모드) ──────
        static void ApplyWindowedPlayerSettings(BenchmarkConfig cfg)
        {
            int w = Mathf.Max(320, cfg.windowedWidth);
            int h = Mathf.Max(240, cfg.windowedHeight);
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.defaultScreenWidth = w;
            PlayerSettings.defaultScreenHeight = h;
            PlayerSettings.resizableWindow = true;
            Debug.Log($"[Benchmark] 창모드로 빌드: {w}x{h}");
        }

        static void ApplyFullscreenPlayerSettings()
        {
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow; // 전체화면(borderless)
            PlayerSettings.defaultIsNativeResolution = true;
            PlayerSettings.resizableWindow = false;
            Debug.Log("[Benchmark] 전체화면으로 빌드");
        }

        // 실행 시에도 화면 모드를 명시(빌드 굽기와 이중 안전장치).
        static string BuildScreenArgs(BenchmarkConfig cfg)
        {
            if (cfg != null && cfg.windowedBuild)
            {
                int w = Mathf.Max(320, cfg.windowedWidth);
                int h = Mathf.Max(240, cfg.windowedHeight);
                return $" -screen-fullscreen 0 -screen-width {w} -screen-height {h}";
            }
            return " -screen-fullscreen 1";
        }

        static string[] BuildSceneList(BenchmarkConfig cfg)
        {
            var set = new List<string>();
            foreach (var p in cfg.scenePaths)
                if (!string.IsNullOrEmpty(p) && !set.Contains(p)) set.Add(p);

            if (set.Count == 0) set.Add("Assets/Scenes/SampleScene.unity");
            return set.ToArray();
        }

        static string BuildSpawnOverrideArgs(BenchmarkConfig cfg)
        {
            if (cfg == null || !cfg.HasSpawnAutomation)
            {
                return "";
            }

            if (cfg.SpawnCaseCount != 1)
            {
                return "";
            }

            BenchmarkSpawnCase spawnCase = cfg.GetSanitizedSpawnCase(0);
            BenchmarkSpawnEntry[] entries = cfg.GetSanitizedSpawnEntries(0);
            if (entries.Length != 1)
            {
                return "";
            }

            BenchmarkSpawnEntry entry = entries[0];
            BenchmarkSwarmType swarmType = cfg.GetSanitizedSwarmType(entry.swarmTypeIndex);
            string typeArg = !string.IsNullOrWhiteSpace(swarmType.id)
                ? swarmType.id
                : entry.swarmTypeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return
                $" -benchSwarmType {QuoteArg(typeArg)}" +
                $" -benchSpawnCount {entry.spawnCount}" +
                $" -benchSoldierCount {entry.soldierCount}" +
                $" -benchSpawnLabel {QuoteArg(spawnCase.label)}";
        }

        static string QuoteArg(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
