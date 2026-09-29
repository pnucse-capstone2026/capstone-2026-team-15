using System;
using System.Collections;
using System.Globalization;
using Swarm;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Swarm.Benchmark
{
    /// <summary>
    /// 벤치마크를 돌리지 않고도 Test_Scene에서 (N, L) 워크로드를 즉시 적용해 보기 위한 경량 도구.
    ///
    /// 실제 스폰은 벤치마크와 동일하게 <see cref="BenchmarkSpawnAutomation"/> 을 그대로 재사용한다.
    /// 즉 N(총 인스턴스), L(스웜 분할) → 팩션당 근접 L/4 + 원거리 L/4, 스웜당 병사 N/L 로 배치된다.
    /// (Test_Scene 서브씬의 LegionBase 2개 기준)
    ///
    /// 사용법:
    /// - 에디터: 씬의 아무 GameObject에 이 컴포넌트를 붙이고 N/L 을 지정한 뒤 Play.
    ///   (메뉴 Tools ▸ Test_Scene Workload Applier 추가 로 바로 추가 가능)
    /// - 런타임 재적용: Play 중 인스펙터에서 컴포넌트 우클릭 ▸ "현재 N/L 로 다시 적용" 으로 재스폰.
    /// - CLI: -tsN, -tsL 인자가 있으면 그 값이 인스펙터 값을 덮어쓴다. -tsQuit 이면 검증 후 종료.
    /// </summary>
    public sealed class TestSceneWorkloadApplier : MonoBehaviour
    {
        [Header("워크로드 (벤치마크와 동일한 정의)")]
        [Tooltip("N: 씬 전체 병사 인스턴스 총합. L 의 배수여야 병사 수가 정수로 떨어진다.")]
        [Min(1)] public int totalInstances = 8000;

        [Tooltip("L: 스웜(유닛) 분할 수. LegionBase 2개 기준 4의 배수(4/8/16) 권장. 스웜당 병사 = N/L.")]
        [Min(1)] public int swarmPartition = 8;

        [Header("동작")]
        [Tooltip("Play 시작 시 자동으로 적용할지 여부.")]
        public bool applyOnStart = true;

        [Tooltip("검증이 끝나면 에디터/플레이어를 종료한다. CLI 헤드리스 실행용. -tsQuit 인자로도 켜진다.")]
        public bool quitWhenDone = false;

        private bool _busy;

        private void Start()
        {
            ApplyCommandLineOverrides();
            if (applyOnStart)
            {
                StartCoroutine(ApplyRoutine());
            }
        }

        // 이 프로젝트는 새 Input System 전용(activeInputHandler=1)이라 레거시 Input 을 쓰지 않는다.
        // 런타임 재적용은 인스펙터 컨텍스트 메뉴로 제공한다.
        [ContextMenu("현재 N/L 로 다시 적용")]
        private void ReapplyFromContextMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[TestWorkload] Play 모드에서만 적용됩니다.");
                return;
            }
            if (_busy)
            {
                Debug.LogWarning("[TestWorkload] 이미 처리 중입니다.");
                return;
            }
            StartCoroutine(ApplyRoutine());
        }

        /// <summary>
        /// 외부 스크립트에서 특정 (N, L) 로 즉시 재적용할 때 사용.
        /// </summary>
        public void Apply(int totalInstancesValue, int swarmPartitionValue)
        {
            totalInstances = Mathf.Max(1, totalInstancesValue);
            swarmPartition = Mathf.Max(1, swarmPartitionValue);
            if (!_busy)
            {
                StartCoroutine(ApplyRoutine());
            }
        }

        private IEnumerator ApplyRoutine()
        {
            _busy = true;

            int n = Mathf.Max(1, totalInstances);
            int l = Mathf.Max(1, swarmPartition);
            Debug.Log($"[TestWorkload] 시작 요청 N={n} L={l} (스웜당 병사={n / l}).");

            if (n % l != 0)
            {
                Debug.LogWarning(
                    $"[TestWorkload] N({n})이 L({l})로 나눠떨어지지 않습니다. " +
                    "스웜당 병사 수가 내림 처리되어 실제 총합이 N보다 작을 수 있습니다.");
            }

            // 1) ECS World 준비 대기.
            float waited = 0f;
            while ((World.DefaultGameObjectInjectionWorld == null ||
                    !World.DefaultGameObjectInjectionWorld.IsCreated) && waited < 20f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                Fail("ECS World 를 찾지 못했습니다.");
                yield break;
            }

            EntityManager entityManager = world.EntityManager;

            // 2) SubScene 스트리밍으로 LegionBase 가 베이킹될 때까지 대기.
            EntityQuery baseQuery =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<LegionBaseTag>());
            waited = 0f;
            while (baseQuery.CalculateEntityCount() == 0 && waited < 20f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            int baseCount = baseQuery.CalculateEntityCount();
            if (baseCount == 0)
            {
                Fail("LegionBase 엔티티가 없습니다. Test_Scene 의 SubScene 로드를 확인하세요.");
                yield break;
            }
            Debug.Log($"[TestWorkload] LegionBase {baseCount}개 확인. 워크로드 적용 시작.");

            // 3) 벤치마크와 동일한 설정을 로드해 스폰 자동화 재사용.
            var config = Resources.Load<BenchmarkConfig>("BenchmarkConfig");
            if (config == null)
            {
                Fail("Resources/BenchmarkConfig 를 찾지 못했습니다.");
                yield break;
            }

            var workload = new BenchmarkWorkload
            {
                totalInstances = n,
                swarmPartition = l
            };
            var automation = new BenchmarkSpawnAutomation(config, workload);

            if (!automation.TryApply(out int appliedBaseCount))
            {
                Fail("BenchmarkSpawnAutomation.TryApply 실패.");
                yield break;
            }

            // 4) 유닛 수/역할 검증 (벤치마크와 동일한 폴링 로직).
            yield return automation.WaitForResult(appliedBaseCount);

            // 5) SwarmInitSystem 이 병사 버퍼를 실제로 구성할 때까지 잠깐 대기해
            //    "실측" 인스턴스 수를 함께 보고한다.
            int intendedSoldiers = automation.Metadata.Soldiers;
            int actualSoldiers = 0;
            waited = 0f;
            while (waited < 5f)
            {
                actualSoldiers = CountSoldierInstances(entityManager);
                if (intendedSoldiers > 0 && actualSoldiers >= intendedSoldiers)
                {
                    break;
                }
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            var meta = automation.Metadata;
            Debug.Log(
                "[TestWorkload] RESULT " +
                $"requestedN={n} requestedL={l} bases={appliedBaseCount} " +
                $"units={meta.ActualUnits} (melee={meta.MeleeUnits} archer={meta.ArcherUnits}) " +
                $"soldiersIntended={intendedSoldiers} soldiersActualBuffers={actualSoldiers} " +
                $"matched={meta.VerificationMatched}");

            _busy = false;

            if (quitWhenDone)
            {
                Quit(meta.VerificationMatched ? 0 : 2);
            }
        }

        private static int CountSoldierInstances(EntityManager entityManager)
        {
            EntityQuery query = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<SoldierAgent>() },
                None = new[] { ComponentType.ReadOnly<Prefab>() }
            });
            using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            query.Dispose();

            int total = 0;
            foreach (Entity entity in entities)
            {
                if (entityManager.HasBuffer<SoldierAgent>(entity))
                {
                    total += entityManager.GetBuffer<SoldierAgent>(entity).Length;
                }
            }
            return total;
        }

        private void Fail(string message)
        {
            Debug.LogError($"[TestWorkload] 실패: {message}");
            _busy = false;
            if (quitWhenDone)
            {
                Quit(3);
            }
        }

        private void ApplyCommandLineOverrides()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (TryGetIntArg(args, "-tsN", out int n))
            {
                totalInstances = Mathf.Max(1, n);
            }
            if (TryGetIntArg(args, "-tsL", out int l))
            {
                swarmPartition = Mathf.Max(1, l);
            }
            if (HasArg(args, "-tsQuit"))
            {
                quitWhenDone = true;
            }
        }

        private static bool HasArg(string[] args, string key)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetIntArg(string[] args, string key, out int value)
        {
            value = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(
                        args[i + 1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out value);
                }
            }
            return false;
        }

        private static void Quit(int exitCode)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(exitCode);
#else
            Application.Quit(exitCode);
#endif
        }

        /// <summary>
        /// CLI(-tsN 지정) 헤드리스 실행에서, 씬에 컴포넌트를 미리 배치하지 않아도
        /// 자동으로 applier 를 생성한다. 대화형(인자 없음) 실행에는 영향을 주지 않는다.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreateForCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!HasArg(args, "-tsN"))
            {
                return;
            }
            if (FindFirstObjectByType<TestSceneWorkloadApplier>() != null)
            {
                return;
            }

            var go = new GameObject("[TestSceneWorkloadApplier]");
            var applier = go.AddComponent<TestSceneWorkloadApplier>();
            applier.applyOnStart = true;
            applier.quitWhenDone = true;
        }
    }
}
