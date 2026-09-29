using System;
using System.Collections.Generic;
using UnityEngine;

namespace Swarm.Benchmark
{
    [Serializable]
    public struct BenchmarkSwarmType
    {
        public string id;
        public string label;
        public GameObject prefab;
    }

    [Serializable]
    public struct BenchmarkSpawnEntry
    {
        public int swarmTypeIndex;
        public int spawnCount;

        [Tooltip("0이면 Prefab의 SwarmParams.SoldierCount 값을 그대로 사용합니다.")]
        public int soldierCount;
    }

    [Serializable]
    public struct BenchmarkSpawnCase
    {
        public string label;
        public int swarmTypeIndex;
        public int spawnCount;
        public int soldierCount;
        public BenchmarkSpawnEntry[] entries;
    }

    /// <summary>
    /// 논문 워크로드 한 점: 총 인스턴스 N, 스웜 분할 L. n_swarm = N/L.
    /// </summary>
    public struct BenchmarkWorkload
    {
        public int totalInstances;  // N
        public int swarmPartition;  // L

        public int SoldiersPerSwarm =>
            swarmPartition > 0 ? totalInstances / swarmPartition : 0;
    }

    /// <summary>
    /// 벤치마크 실행 설정. Resources 폴더에 두면 Standalone 빌드에서도
    /// Resources.Load 로 로드된다. (Assets/Benchmark/Resources/BenchmarkConfig.asset)
    /// </summary>
    [CreateAssetMenu(fileName = "BenchmarkConfig", menuName = "Swarm/Benchmark Config")]
    public sealed class BenchmarkConfig : ScriptableObject
    {
        [Header("측정 대상 씬 (씬 경로)")]
        [Tooltip("예: Assets/Scenes/Unit/UnitScene.unity — 에디터 창에서 드래그로 채우면 자동으로 경로가 들어갑니다.")]
        public string[] scenePaths = new string[0];

        [Header("실행 파라미터")]
        [Tooltip("한 번 실행당 측정 시간(초). 1분=60, 2분=120")]
        [Min(1f)] public float durationSeconds = 60f;

        [Tooltip("각 씬(그리고 케이스)마다 반복 횟수")]
        [Min(1)] public int repeats = 5;

        [Tooltip("측정 시작 전 버리는 워밍업 시간(초). 셰이더 컴파일/스트리밍 안정화용")]
        [Min(0f)] public float warmupSeconds = 3f;

        [Tooltip("반복 사이 대기 시간(초)")]
        [Min(0f)] public float betweenRunsSeconds = 1f;

        [Header("결정성(Determinism)")]
        [Tooltip("측정 동안 VSync 끔 → 프레임 상한 없이 최대 스루풋 측정")]
        public bool disableVSync = true;

        [Tooltip("-1 = 프레임 상한 없음")]
        public int targetFrameRate = -1;

        [Tooltip("끄면 원거리 공격 판정과 애니메이션은 유지하고 투사체 GameObject만 생성하지 않습니다.")]
        public bool renderRangedProjectiles = true;

        [Header("옵션: 렌더 케이스 스윕")]
        [Tooltip("켜면 아래 케이스들을 각각 설정해 씬을 반복 측정합니다.")]
        public bool sweepRenderCases = false;

        public SwarmRenderPipelineCase[] renderCases = new[]
        {
            SwarmRenderPipelineCase.RenderMeshIndirect_Integrated,
            SwarmRenderPipelineCase.RenderMeshIndirect_Split,
            SwarmRenderPipelineCase.BRG_Integrated,
            SwarmRenderPipelineCase.BRG_Split,
        };

        [Header("논문 워크로드 (N×L)")]
        [Tooltip("켜면 아래 N(총 인스턴스)×L(스웜 분할) 그리드를 자동 생성해 각 조합마다 스폰을 적용합니다. spawnCases 대신 사용됩니다.")]
        public bool useWorkloadGrid = false;

        [Tooltip("총 인스턴스 수 N 레벨")]
        public int[] instanceCounts = { 2000, 4000, 8000, 16000 };

        [Tooltip("스웜 분할 수준 L 레벨")]
        public int[] swarmPartitions = { 4, 8, 16 };

        [Tooltip("팩션(LegionBase) 수. 팩션당 근접 L/(2·factionCount) + 원거리 L/(2·factionCount) 스웜.")]
        [Min(1)] public int factionCount = 2;

        [Tooltip("근접 스웜 타입 인덱스 (swarmTypes / LegionUnitPrefabOption)")]
        public int meleeSwarmTypeIndex = 0;

        [Tooltip("원거리 스웜 타입 인덱스")]
        public int rangedSwarmTypeIndex = 1;

        [Tooltip("렌더러 인스턴스 버퍼 사전 할당 용량. 최대 N 이상으로 두면 측정 중 재할당을 피함.")]
        public int initialInstanceCapacity = 16384;

        [Tooltip("단계·구조 지표 주기 샘플링 간격(초). 논문 기본 1초.")]
        [Min(0.05f)] public float periodicSampleIntervalSeconds = 1f;

        [Tooltip("설정 검증/스폰 실패 시 같은 조건 재측정 최대 횟수.")]
        [Min(0)] public int validationRetries = 3;

        [Header("Spawn Automation")]
        [Tooltip("켜면 각 렌더 케이스 실행 전에 타입별 논리 유닛과 인스턴스 수를 적용합니다.")]
        public bool overrideSpawnSettings = false;

        public BenchmarkSwarmType[] swarmTypes = Array.Empty<BenchmarkSwarmType>();
        public BenchmarkSpawnCase[] spawnCases = Array.Empty<BenchmarkSpawnCase>();

        [Header("출력")]
        [Tooltip("비우면 <persistentDataPath>/BenchmarkResults 에 저장")]
        public string outputFolderOverride = "";

        [Tooltip("프레임별 원본 CSV 저장 여부 (그래프용, 용량 큼)")]
        public bool writePerFrameCsv = true;

        [Tooltip("각 렌더러의 구조 계측값을 BenchmarkRunner가 수집하여 프레임별 CSV와 요약에 포함합니다.")]
        public bool enableRendererInstrumentation = true;

        [Header("빌드 (테스트용)")]
        [Tooltip("켜면 Standalone 빌드를 전체화면이 아니라 창모드로 만들고 실행합니다. 관찰/디버그용이며, " +
                 "창모드는 present 경로가 달라 전체화면 측정 결과와 같은 표에 섞지 마세요.")]
        public bool windowedBuild = false;

        [Tooltip("창모드 빌드 가로 해상도")]
        [Min(320)] public int windowedWidth = 1920;

        [Tooltip("창모드 빌드 세로 해상도")]
        [Min(240)] public int windowedHeight = 1080;

        public bool HasSpawnAutomation =>
            overrideSpawnSettings &&
            swarmTypes != null && swarmTypes.Length > 0 &&
            spawnCases != null && spawnCases.Length > 0;

        public int SpawnCaseCount =>
            HasSpawnAutomation ? Mathf.Max(1, spawnCases.Length) : 1;

        public BenchmarkSpawnCase GetSanitizedSpawnCase(int index)
        {
            if (spawnCases == null || spawnCases.Length == 0)
            {
                return new BenchmarkSpawnCase
                {
                    label = "Default",
                    spawnCount = 1,
                    entries = Array.Empty<BenchmarkSpawnEntry>()
                };
            }

            int safeIndex = Mathf.Clamp(index, 0, spawnCases.Length - 1);
            BenchmarkSpawnCase spawnCase = spawnCases[safeIndex];
            int typeCount = swarmTypes != null ? swarmTypes.Length : 0;

            if (string.IsNullOrWhiteSpace(spawnCase.label))
                spawnCase.label = $"Spawn {safeIndex + 1}";

            spawnCase.swarmTypeIndex = typeCount > 0
                ? Mathf.Clamp(spawnCase.swarmTypeIndex, 0, typeCount - 1)
                : 0;
            spawnCase.spawnCount = Mathf.Max(0, spawnCase.spawnCount);
            spawnCase.soldierCount = Mathf.Max(0, spawnCase.soldierCount);
            spawnCase.entries = SanitizeSpawnEntries(spawnCase);
            return spawnCase;
        }

        public BenchmarkSpawnEntry[] GetSanitizedSpawnEntries(int index) =>
            GetSanitizedSpawnCase(index).entries;

        private BenchmarkSpawnEntry[] SanitizeSpawnEntries(
            BenchmarkSpawnCase spawnCase)
        {
            int typeCount = swarmTypes != null ? swarmTypes.Length : 0;
            BenchmarkSpawnEntry[] source = spawnCase.entries;
            if (source == null || source.Length == 0)
            {
                source = new[]
                {
                    new BenchmarkSpawnEntry
                    {
                        swarmTypeIndex = spawnCase.swarmTypeIndex,
                        spawnCount = spawnCase.spawnCount,
                        soldierCount = spawnCase.soldierCount
                    }
                };
            }

            var result = new BenchmarkSpawnEntry[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                BenchmarkSpawnEntry entry = source[i];
                entry.swarmTypeIndex = typeCount > 0
                    ? Mathf.Clamp(entry.swarmTypeIndex, 0, typeCount - 1)
                    : 0;
                entry.spawnCount = Mathf.Max(0, entry.spawnCount);
                entry.soldierCount = Mathf.Max(0, entry.soldierCount);
                result[i] = entry;
            }

            return result;
        }

        public BenchmarkSwarmType GetSanitizedSwarmType(int index)
        {
            if (swarmTypes == null || swarmTypes.Length == 0)
            {
                return new BenchmarkSwarmType
                {
                    id = "type-1",
                    label = "Type 1"
                };
            }

            int safeIndex = Mathf.Clamp(index, 0, swarmTypes.Length - 1);
            BenchmarkSwarmType type = swarmTypes[safeIndex];
            if (string.IsNullOrWhiteSpace(type.id))
                type.id = $"type-{safeIndex + 1}";
            if (string.IsNullOrWhiteSpace(type.label))
                type.label = type.id;
            return type;
        }

        public bool HasWorkloadGrid =>
            useWorkloadGrid &&
            instanceCounts != null && instanceCounts.Length > 0 &&
            swarmPartitions != null && swarmPartitions.Length > 0;

        /// <summary>
        /// N×L 그리드를 정합성 검증(N%L==0, N%4==0, L%(2·factionCount)==0)하며 열거한다.
        /// 위반 조합은 경고 후 제외한다.
        /// </summary>
        public List<BenchmarkWorkload> EnumerateWorkloads()
        {
            var list = new List<BenchmarkWorkload>();
            if (!HasWorkloadGrid) return list;

            int f = Mathf.Max(1, factionCount);
            foreach (int n in instanceCounts)
            {
                foreach (int l in swarmPartitions)
                {
                    if (n <= 0 || l <= 0) continue;

                    bool ok = (n % l == 0) && (n % 4 == 0) && (l % (2 * f) == 0);
                    if (!ok)
                    {
                        Debug.LogWarning(
                            $"[Benchmark] 워크로드 (N={n}, L={l}) 정합성 위반 " +
                            $"(N%L, N%4, L%(2·{f}) 중 하나) → 제외");
                        continue;
                    }

                    list.Add(new BenchmarkWorkload
                    {
                        totalInstances = n,
                        swarmPartition = l
                    });
                }
            }
            return list;
        }

        public int WorkloadCount => EnumerateWorkloads().Count;

        /// <summary> 대략적인 총 소요 시간(초) 추정 — 에디터 표시용. </summary>
        public float EstimatedTotalSeconds
        {
            get
            {
                int scenes = scenePaths != null ? scenePaths.Length : 0;
                int caseMul = (sweepRenderCases && renderCases != null && renderCases.Length > 0)
                    ? renderCases.Length : 1;
                int workloadMul = HasWorkloadGrid
                    ? Mathf.Max(1, WorkloadCount)
                    : (HasSpawnAutomation ? SpawnCaseCount : 1);
                float perRun = warmupSeconds + durationSeconds + betweenRunsSeconds;
                return Mathf.Max(0, scenes) * Mathf.Max(1, repeats) * caseMul * workloadMul * perRun;
            }
        }
    }
}
