using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Swarm.Benchmark.EditorTools
{
    public sealed class BenchmarkWindow : EditorWindow
    {
        const string ConfigPath = "Assets/Benchmark/Resources/BenchmarkConfig.asset";

        BenchmarkConfig _config;
        Vector2 _scroll;

        [MenuItem("Tools/Swarm Benchmark")]
        public static void Open()
        {
            var w = GetWindow<BenchmarkWindow>("Swarm Benchmark");
            w.minSize = new Vector2(420, 520);
            w.Show();
        }

        void OnEnable() => _config = LoadOrCreateConfig();

        void OnGUI()
        {
            if (_config == null) _config = LoadOrCreateConfig();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("측정 대상 씬", EditorStyles.boldLabel);
            DrawSceneList();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("실행 파라미터", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _config.durationSeconds = EditorGUILayout.FloatField("측정 시간(초)", _config.durationSeconds);
                if (GUILayout.Button("1분", GUILayout.Width(48))) _config.durationSeconds = 60f;
                if (GUILayout.Button("2분", GUILayout.Width(48))) _config.durationSeconds = 120f;
            }

            _config.repeats = Mathf.Max(1, EditorGUILayout.IntField("반복 횟수", _config.repeats));
            _config.warmupSeconds = EditorGUILayout.FloatField("워밍업(초)", _config.warmupSeconds);
            _config.betweenRunsSeconds = EditorGUILayout.FloatField("반복 간 대기(초)", _config.betweenRunsSeconds);

            EditorGUILayout.Space(4);
            _config.disableVSync = EditorGUILayout.Toggle("VSync 끄기(측정 중)", _config.disableVSync);
            _config.targetFrameRate = EditorGUILayout.IntField("targetFrameRate (-1=무제한)", _config.targetFrameRate);
            _config.renderRangedProjectiles = EditorGUILayout.Toggle(
                "원거리 투사체 렌더링",
                _config.renderRangedProjectiles);

            EditorGUILayout.HelpBox(
                "끄면 원거리 공격 로직과 애니메이션은 유지하고 투사체 비주얼 생성만 제외합니다.",
                MessageType.None);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("옵션: 렌더 케이스 스윕", EditorStyles.boldLabel);
            _config.sweepRenderCases = EditorGUILayout.Toggle("케이스 스윕 사용", _config.sweepRenderCases);
            if (_config.sweepRenderCases)
            {
                EditorGUI.indentLevel++;
                DrawCaseToggles();
                EditorGUILayout.HelpBox(
                    "RenderMeshIndirect 통합/분할과 BRG 통합/분할 조건을 선택합니다. " +
                    "각 씬에는 해당 실험용 렌더러와 BenchmarkRunner 연동이 구성되어 있어야 합니다.",
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);
            DrawWorkload();

            EditorGUILayout.Space(8);
            DrawSpawnAutomation();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("출력", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _config.outputFolderOverride = EditorGUILayout.TextField("출력 폴더(비우면 기본)", _config.outputFolderOverride);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string p = EditorUtility.OpenFolderPanel("출력 폴더 선택", "", "");
                    if (!string.IsNullOrEmpty(p)) _config.outputFolderOverride = p;
                }
            }
            _config.writePerFrameCsv = EditorGUILayout.Toggle("프레임별 CSV 저장", _config.writePerFrameCsv);
            _config.enableRendererInstrumentation =
                EditorGUILayout.Toggle(
                    "렌더러 구조 계측 포함",
                    _config.enableRendererInstrumentation);

            EditorGUILayout.HelpBox(
                "렌더러 구조 계측은 렌더러 내부의 제출 구조 값만 읽으며, " +
                "CSV 파일 저장은 BenchmarkRunner가 측정 종료 후 수행합니다.",
                MessageType.None);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("빌드 (테스트용)", EditorStyles.boldLabel);
            _config.windowedBuild = EditorGUILayout.Toggle("창모드로 빌드", _config.windowedBuild);
            if (_config.windowedBuild)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUILayout.HorizontalScope())
                {
                    _config.windowedWidth = Mathf.Max(320, EditorGUILayout.IntField("가로", _config.windowedWidth));
                    _config.windowedHeight = Mathf.Max(240, EditorGUILayout.IntField("세로", _config.windowedHeight));
                    if (GUILayout.Button("1080p", GUILayout.Width(56)))
                    {
                        _config.windowedWidth = 1920;
                        _config.windowedHeight = 1080;
                    }
                    if (GUILayout.Button("720p", GUILayout.Width(52)))
                    {
                        _config.windowedWidth = 1280;
                        _config.windowedHeight = 720;
                    }
                }
                EditorGUILayout.HelpBox(
                    "전체화면 대신 창모드로 빌드/실행합니다(관찰·디버그용). 창모드는 DWM 합성 경로라 " +
                    "절대 GPU 타이밍이 전체화면과 다를 수 있으니, 전체화면 측정 결과와 같은 표에 섞지 마세요. " +
                    "E1~E4 상대 비교에는 문제없습니다.",
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(_config);
            }

            EditorGUILayout.Space(6);
            if (GUILayout.Button("논문(1.610) 프리셋 적용", GUILayout.Height(24)))
                ApplyPaperPreset();

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                $"예상 총 소요: 약 {Mathf.RoundToInt(_config.EstimatedTotalSeconds)}초 " +
                $"(≈ {_config.EstimatedTotalSeconds / 60f:0.0}분)", MessageType.None);

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                // 무거운 작업(폴더 패널·BuildPlayer·EnterPlaymode)을 OnGUI 안에서 바로 실행하면
                // 레이아웃 스택이 끊겨 "EndLayoutGroup/GUIClips" 경고가 난다.
                // delayCall 로 OnGUI 종료 후 실행해 경고를 없앤다.
                if (GUILayout.Button("▶  Play 모드에서 실행", GUILayout.Height(32)))
                    EditorApplication.delayCall += RunInPlayMode;

                if (GUILayout.Button("⚙  개발 빌드 후 실행 (Standalone)", GUILayout.Height(28)))
                    EditorApplication.delayCall += () => BenchmarkBuilder.BuildAndRun(SaveAndGet());
            }

            if (GUILayout.Button("📂  결과 폴더 열기"))
                OpenResultsFolder();

            EditorGUILayout.EndScrollView();
        }

        // ── Scene list ─────────────────────────────────────────────
        void DrawSceneList()
        {
            var paths = new List<string>(_config.scenePaths ?? new string[0]);

            int removeAt = -1;
            for (int i = 0; i < paths.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var current = AssetDatabase.LoadAssetAtPath<SceneAsset>(paths[i]);
                    var next = (SceneAsset)EditorGUILayout.ObjectField(current, typeof(SceneAsset), false);
                    if (next != current)
                        paths[i] = next != null ? AssetDatabase.GetAssetPath(next) : "";

                    if (GUILayout.Button("✕", GUILayout.Width(24))) removeAt = i;
                }
            }
            if (removeAt >= 0) paths.RemoveAt(removeAt);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ 씬 추가")) paths.Add("");
                if (GUILayout.Button("현재 열린 씬 추가"))
                {
                    string p = EditorSceneManager.GetActiveScene().path;
                    if (!string.IsNullOrEmpty(p) && !paths.Contains(p)) paths.Add(p);
                }
            }

            _config.scenePaths = paths.ToArray();
        }

        // ── Case toggles ───────────────────────────────────────────
        static readonly SwarmRenderPipelineCase[] AllCases =
        {
            SwarmRenderPipelineCase.RenderMeshIndirect_Integrated,
            SwarmRenderPipelineCase.RenderMeshIndirect_Split,
            SwarmRenderPipelineCase.BRG_Integrated,
            SwarmRenderPipelineCase.BRG_Split,
        };

        void DrawCaseToggles()
        {
            var set = new HashSet<SwarmRenderPipelineCase>(_config.renderCases ?? new SwarmRenderPipelineCase[0]);
            var result = new List<SwarmRenderPipelineCase>();
            foreach (var c in AllCases)
            {
                bool on = EditorGUILayout.ToggleLeft(SwarmRenderPipelineCaseUtility.GetCaseLabel(c), set.Contains(c));
                if (on) result.Add(c);
            }
            _config.renderCases = result.ToArray();
        }

        void DrawSpawnAutomation()
        {
            EditorGUILayout.LabelField("스폰 자동화", EditorStyles.boldLabel);
            _config.overrideSpawnSettings = EditorGUILayout.Toggle(
                "스폰 설정 오버라이드",
                _config.overrideSpawnSettings);

            if (!_config.overrideSpawnSettings)
            {
                EditorGUILayout.HelpBox(
                    "꺼져 있으면 씬의 LegionBase 설정을 그대로 사용합니다.",
                    MessageType.Info);
                return;
            }

            var serializedConfig = new SerializedObject(_config);
            serializedConfig.Update();
            EditorGUILayout.PropertyField(
                serializedConfig.FindProperty("swarmTypes"),
                new GUIContent("스웜 타입"),
                true);
            EditorGUILayout.PropertyField(
                serializedConfig.FindProperty("spawnCases"),
                new GUIContent("스폰 케이스"),
                true);
            serializedConfig.ApplyModifiedProperties();

            EditorGUILayout.HelpBox(
                "각 케이스에서 타입별 논리 유닛 수와 논리 유닛당 인스턴스 수를 설정합니다. " +
                "soldierCount가 0이면 프리팹 기본값을 사용합니다.",
                MessageType.None);
        }

        // ── Workload (N×L) ─────────────────────────────────────────
        void DrawWorkload()
        {
            EditorGUILayout.LabelField("논문 워크로드 (N×L)", EditorStyles.boldLabel);
            _config.useWorkloadGrid = EditorGUILayout.Toggle(
                "워크로드 그리드 사용", _config.useWorkloadGrid);

            if (!_config.useWorkloadGrid)
            {
                EditorGUILayout.HelpBox(
                    "켜면 N(총 인스턴스)×L(스웜 분할) 그리드를 자동 생성해 스폰합니다(spawnCases 대신).",
                    MessageType.Info);
                return;
            }

            var so = new SerializedObject(_config);
            so.Update();
            EditorGUILayout.PropertyField(
                so.FindProperty("instanceCounts"), new GUIContent("N 레벨 (총 인스턴스)"), true);
            EditorGUILayout.PropertyField(
                so.FindProperty("swarmPartitions"), new GUIContent("L 레벨 (스웜 분할)"), true);
            so.ApplyModifiedProperties();

            _config.factionCount = Mathf.Max(1, EditorGUILayout.IntField("팩션 수", _config.factionCount));
            _config.meleeSwarmTypeIndex = EditorGUILayout.IntField("근접 타입 인덱스", _config.meleeSwarmTypeIndex);
            _config.rangedSwarmTypeIndex = EditorGUILayout.IntField("원거리 타입 인덱스", _config.rangedSwarmTypeIndex);
            _config.initialInstanceCapacity = EditorGUILayout.IntField("렌더러 사전할당 용량", _config.initialInstanceCapacity);
            _config.periodicSampleIntervalSeconds = EditorGUILayout.FloatField("주기 샘플 간격(초)", _config.periodicSampleIntervalSeconds);
            _config.validationRetries = Mathf.Max(0, EditorGUILayout.IntField("검증 실패 재측정 횟수", _config.validationRetries));

            EditorGUILayout.HelpBox(
                $"유효 조합 {_config.WorkloadCount}개 (N%L·N%4·L%(2·팩션) 통과). " +
                "팩션당 근접 L/4 + 원거리 L/4, 스웜당 병사=N/L. " +
                "렌더러 initialInstanceCapacity를 최대 N 이상으로 두면 재할당이 워밍업에 흡수됩니다.",
                MessageType.None);
        }

        void ApplyPaperPreset()
        {
            _config.durationSeconds = 60f;
            _config.warmupSeconds = 3f;
            _config.betweenRunsSeconds = 1f;
            _config.repeats = 5;
            _config.disableVSync = true;
            _config.targetFrameRate = -1;
            _config.renderRangedProjectiles = false;
            _config.windowedBuild = false; // 실측 프리셋은 전체화면(present 경로 일관)

            _config.sweepRenderCases = true;
            _config.renderCases = new[]
            {
                SwarmRenderPipelineCase.RenderMeshIndirect_Integrated,
                SwarmRenderPipelineCase.RenderMeshIndirect_Split,
                SwarmRenderPipelineCase.BRG_Integrated,
                SwarmRenderPipelineCase.BRG_Split,
            };

            _config.useWorkloadGrid = true;
            _config.instanceCounts = new[] { 2000, 4000, 8000, 16000 };
            _config.swarmPartitions = new[] { 4, 8, 16 };
            _config.factionCount = 2;
            _config.meleeSwarmTypeIndex = 0;
            _config.rangedSwarmTypeIndex = 1;
            _config.initialInstanceCapacity = 16384;
            _config.periodicSampleIntervalSeconds = 1f;
            _config.validationRetries = 3;
            _config.enableRendererInstrumentation = true;
            _config.writePerFrameCsv = true;

            const string testScene = "Assets/Scenes/Test/Test_Scene.unity";
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(testScene) != null)
                _config.scenePaths = new[] { testScene };
            else
                Debug.LogWarning($"[Benchmark] {testScene} 를 찾지 못했습니다. 대상 씬을 수동 지정하세요.");

            EditorUtility.SetDirty(_config);
            AssetDatabase.SaveAssets();
            Debug.Log("[Benchmark] 논문(1.610) 프리셋 적용됨: N×L 스윕 + E1~E4 + 60s×5, 투사체 off.");
        }

        // ── Actions ────────────────────────────────────────────────
        void RunInPlayMode()
        {
            var cfg = SaveAndGet();
            if (cfg.scenePaths == null || cfg.scenePaths.Length == 0)
            {
                EditorUtility.DisplayDialog("Swarm Benchmark", "측정할 씬을 1개 이상 추가하세요.", "확인");
                return;
            }

            PlayerSettings.enableFrameTimingStats = true;       // FrameTimingManager 활성화
            EnsureScenesInBuild(cfg.scenePaths);                // Play 모드 LoadSceneAsync 를 위해 필요
            SessionState.SetBool(BenchmarkBootstrap.RunOnPlayKey, true);
            EditorApplication.EnterPlaymode();
        }

        void OpenResultsFolder()
        {
            string folder = string.IsNullOrEmpty(_config.outputFolderOverride)
                ? Path.Combine(Application.persistentDataPath, "BenchmarkResults")
                : _config.outputFolderOverride;
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        BenchmarkConfig SaveAndGet()
        {
            EditorUtility.SetDirty(_config);
            AssetDatabase.SaveAssets();
            return _config;
        }

        // ── Helpers ────────────────────────────────────────────────
        static void EnsureScenesInBuild(string[] paths)
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool changed = false;
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                int idx = list.FindIndex(s => s.path == p);
                if (idx < 0) { list.Add(new EditorBuildSettingsScene(p, true)); changed = true; }
                else if (!list[idx].enabled) { list[idx] = new EditorBuildSettingsScene(p, true); changed = true; }
            }
            if (changed) EditorBuildSettings.scenes = list.ToArray();
        }

        static BenchmarkConfig LoadOrCreateConfig()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<BenchmarkConfig>(ConfigPath);
            if (cfg != null) return cfg;

            Directory.CreateDirectory("Assets/Benchmark/Resources");
            cfg = CreateInstance<BenchmarkConfig>();
            cfg.scenePaths = new[] { "Assets/Scenes/Unit/UnitScene.unity" };
            AssetDatabase.CreateAsset(cfg, ConfigPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return cfg;
        }
    }
}
