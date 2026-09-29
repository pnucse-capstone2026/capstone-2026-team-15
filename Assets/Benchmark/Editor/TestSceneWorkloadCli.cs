using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Swarm.Benchmark.EditorTools
{
    /// <summary>
    /// <see cref="TestSceneWorkloadApplier"/> 를 위한 에디터 지원.
    /// - 대화형: 메뉴로 현재 씬에 applier 를 추가한다.
    /// - 헤드리스: Unity CLI 의 -executeMethod 로 씬을 열고 Play 하여 (N,L) 적용 결과를 로그로 남긴다.
    /// </summary>
    public static class TestSceneWorkloadCli
    {
        private const string DefaultScenePath = "Assets/Scenes/Test/Test_Scene.unity";

        // 주의: 기존 벤치마크 창은 [MenuItem("Tools/Swarm Benchmark")] 라는 "명령" 항목이다.
        // 여기에 "Tools/Swarm Benchmark/..." 하위 항목을 만들면 같은 이름이 명령이자 폴더가 되어
        // 기존 창 열기 메뉴가 가려진다. 그래서 별도 최상위 항목으로 둔다.
        [MenuItem("Tools/Test_Scene Workload Applier 추가")]
        public static void AddApplierToOpenScene()
        {
            var existing = UnityEngine.Object.FindFirstObjectByType<TestSceneWorkloadApplier>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing);
                Debug.Log("[TestWorkload] 이미 씬에 TestSceneWorkloadApplier 가 있습니다.");
                return;
            }

            var go = new GameObject("[TestSceneWorkloadApplier]");
            go.AddComponent<TestSceneWorkloadApplier>();
            Undo.RegisterCreatedObjectUndo(go, "Add Test_Scene Workload Applier");
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            Debug.Log(
                "[TestWorkload] TestSceneWorkloadApplier 추가됨. " +
                "인스펙터에서 N/L 을 설정하고 Play 하세요.");
        }

        /// <summary>
        /// CLI 진입점. 예:
        /// Unity.exe -batchmode -projectPath &lt;path&gt; -executeMethod
        ///   Swarm.Benchmark.EditorTools.TestSceneWorkloadCli.RunHeadless
        ///   -tsN 8000 -tsL 8 -tsQuit
        /// (-quit 은 붙이지 말 것. applier 가 검증 후 종료 코드를 정해 스스로 종료한다.)
        /// </summary>
        public static void RunHeadless()
        {
            string[] args = Environment.GetCommandLineArgs();
            string scenePath = GetStringArg(args, "-tsScene") ?? DefaultScenePath;

            Debug.Log(
                $"[TestWorkload] RunHeadless 시작. scene={scenePath} " +
                $"N={GetStringArg(args, "-tsN") ?? "(인스펙터/기본)"} " +
                $"L={GetStringArg(args, "-tsL") ?? "(인스펙터/기본)"}");

            try
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TestWorkload] 씬 열기 실패: {scenePath}\n{e}");
                EditorApplication.Exit(5);
                return;
            }

            // -tsN 이 있으면 applier 가 부트스트랩으로 자동 생성되어 적용/검증/종료까지 처리한다.
            // 안전장치로, -tsN 이 없어도 씬에 applier 가 이미 있으면 그대로 Play 한다.
            EditorApplication.EnterPlaymode();
        }

        private static string GetStringArg(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }
    }
}
