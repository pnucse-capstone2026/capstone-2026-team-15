using System;
using System.Globalization;
using UnityEngine;

namespace Swarm.Benchmark
{
    /// <summary>
    /// Starts benchmark runs from either the editor window or command-line player args.
    /// </summary>
    public static class BenchmarkBootstrap
    {
        public const string RunOnPlayKey = "SwarmBenchmark.RunOnPlay";
        const string ResourceName = "BenchmarkConfig";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart()
        {
#if UNITY_EDITOR
            TryStartEditorRequested("runtime bootstrap");
#else
            if (!HasArg("-runBenchmark")) return;

            var cfg = LoadConfig();
            if (cfg == null)
            {
                Debug.LogError("[Benchmark] Resources/BenchmarkConfig asset not found.");
                return;
            }

            ApplyCommandLineOverrides(cfg);
            BenchmarkRunner.Create(cfg);
#endif
        }

#if UNITY_EDITOR
        public static bool TryStartEditorRequested(string source)
        {
            bool requested =
                UnityEditor.SessionState.GetBool(RunOnPlayKey, false) ||
                UnityEditor.EditorPrefs.GetBool(RunOnPlayKey, false);

            if (!requested)
            {
                return false;
            }

            UnityEditor.SessionState.SetBool(RunOnPlayKey, false);
            UnityEditor.EditorPrefs.SetBool(RunOnPlayKey, false);

            if (UnityEngine.Object.FindFirstObjectByType<BenchmarkRunner>() != null)
            {
                return false;
            }

            var cfg = LoadConfig();
            if (cfg == null)
            {
                Debug.LogError("[Benchmark] Resources/BenchmarkConfig asset not found.");
                return false;
            }

            Debug.Log($"[Benchmark] Starting runner from {source}.");
            BenchmarkRunner.Create(cfg);
            return true;
        }
#endif

        static BenchmarkConfig LoadConfig()
        {
            var asset = Resources.Load<BenchmarkConfig>(ResourceName);
            return asset != null ? UnityEngine.Object.Instantiate(asset) : null;
        }

        static bool HasArg(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static string GetArg(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        static void ApplyCommandLineOverrides(BenchmarkConfig cfg)
        {
            string d = GetArg("-benchDuration");
            if (float.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out float dv))
            {
                cfg.durationSeconds = dv;
            }

            string w = GetArg("-benchWarmup");
            if (float.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out float wv))
            {
                cfg.warmupSeconds = wv;
            }

            string r = GetArg("-benchRepeats");
            if (int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rv))
            {
                cfg.repeats = rv;
            }

            string o = GetArg("-benchOutput");
            if (!string.IsNullOrEmpty(o))
            {
                cfg.outputFolderOverride = o;
            }

            TryApplySpawnCommandLineOverrides(cfg);
        }

        static void TryApplySpawnCommandLineOverrides(BenchmarkConfig cfg)
        {
            string countArg = GetArg("-benchSpawnCount");
            if (string.IsNullOrEmpty(countArg))
            {
                return;
            }

            if (!int.TryParse(countArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int spawnCount))
            {
                Debug.LogWarning($"[Benchmark] Invalid -benchSpawnCount value: {countArg}");
                return;
            }

            spawnCount = Mathf.Max(0, spawnCount);
            int soldierCount = 0;
            string soldierCountArg = GetArg("-benchSoldierCount");
            if (!string.IsNullOrEmpty(soldierCountArg) &&
                !int.TryParse(soldierCountArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out soldierCount))
            {
                Debug.LogWarning($"[Benchmark] Invalid -benchSoldierCount value: {soldierCountArg}");
                soldierCount = 0;
            }

            soldierCount = Mathf.Max(0, soldierCount);

            string typeArg = GetArg("-benchSwarmType");
            int swarmTypeIndex = ResolveSwarmTypeIndex(cfg, typeArg);
            string typeId = GetSwarmTypeId(cfg, swarmTypeIndex);

            string label = GetArg("-benchSpawnLabel");
            if (string.IsNullOrWhiteSpace(label))
            {
                label = $"CLI_{typeId}_count{spawnCount}";
            }

            cfg.overrideSpawnSettings = true;
            cfg.spawnCases = new[]
            {
                new BenchmarkSpawnCase
                {
                    label = label,
                    swarmTypeIndex = swarmTypeIndex,
                    spawnCount = spawnCount,
                    soldierCount = soldierCount,
                    entries = new[]
                    {
                        new BenchmarkSpawnEntry
                        {
                            swarmTypeIndex = swarmTypeIndex,
                            spawnCount = spawnCount,
                            soldierCount = soldierCount
                        }
                    }
                }
            };

            Debug.Log(
                $"[Benchmark] CLI spawn override type={typeId} index={swarmTypeIndex} " +
                $"count={spawnCount} label={label}");
        }

        static int ResolveSwarmTypeIndex(BenchmarkConfig cfg, string value)
        {
            int typeCount = cfg.swarmTypes != null ? cfg.swarmTypes.Length : 0;
            if (typeCount <= 0)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                if (index >= 0 && index < typeCount)
                {
                    return index;
                }

                Debug.LogWarning($"[Benchmark] -benchSwarmType index out of range: {value}. Falling back to 0.");
                return 0;
            }

            for (int i = 0; i < typeCount; i++)
            {
                BenchmarkSwarmType type = cfg.GetSanitizedSwarmType(i);
                if (string.Equals(type.id, value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.label, value, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            Debug.LogWarning($"[Benchmark] Unknown -benchSwarmType value: {value}. Falling back to 0.");
            return 0;
        }

        static string GetSwarmTypeId(BenchmarkConfig cfg, int index)
        {
            if (cfg.swarmTypes == null || cfg.swarmTypes.Length == 0)
            {
                return "type-1";
            }

            BenchmarkSwarmType type = cfg.GetSanitizedSwarmType(index);
            return string.IsNullOrWhiteSpace(type.id)
                ? $"type-{index + 1}"
                : type.id;
        }
    }
}
