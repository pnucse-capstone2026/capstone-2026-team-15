using System;
using Unity.Profiling;
using UnityEngine;

namespace Swarm.Benchmark
{
    /// <summary>
    /// 1초 주기로 수집하는 단계·구조 지표(논문 표5).
    /// 프레임 시간(wall)은 매 프레임 SampleFrameWallMs()로 별도 수집한다.
    /// </summary>
    public struct PeriodicSample
    {
        // FrameTimingManager (하드웨어 타이밍, 단위 ms)
        public double cpuFrameMs;       // CPU 전체 프레임 시간
        public double gpuFrameMs;       // GPU 프레임 시간
        public double mainThreadMs;     // 메인 스레드
        public double renderThreadMs;   // 렌더 스레드
        public double presentWaitMs;    // Present 대기(=GPU 바운드 지표)

        // ProfilerRecorder (Unity가 관측한 값)
        public long drawCalls;
        public long setPassCalls;
        public long batches;
        public long triangles;
        public double sysMemMB;
        public double gcMemMB;
    }

    /// <summary>
    /// FrameTimingManager + ProfilerRecorder 래퍼.
    /// Begin() → 매 프레임 SampleFrameWallMs() + 1초마다 SamplePeriodic() → Dispose().
    /// </summary>
    public sealed class FrameSampler : IDisposable
    {
        readonly FrameTiming[] _timings = new FrameTiming[1];

        ProfilerRecorder _drawCalls;
        ProfilerRecorder _setPass;
        ProfilerRecorder _batches;
        ProfilerRecorder _triangles;
        ProfilerRecorder _sysMem;
        ProfilerRecorder _gcMem;

        bool _started;

        /// <summary> Frame Timing Stats 가 켜져 있어 CPU/GPU ms 를 얻을 수 있는지. </summary>
        public bool FrameTimingSupported { get; private set; }

        public void Begin()
        {
            // 카운터 이름은 Unity Profiler 의 "Available Counters" 에서 확인/조정 가능.
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _setPass   = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _batches   = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _sysMem    = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            _gcMem     = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");

            FrameTimingSupported = FrameTimingManager.IsFeatureEnabled();
            _started = true;
        }

        static long Read(ProfilerRecorder r) => (r.Valid && r.Count > 0) ? r.LastValue : 0L;

        /// <summary>
        /// 매 프레임 호출. 최소 작업만 수행한다 — 벽시계 프레임 시간(ms) 하나만 읽어 반환.
        /// 파일 I/O·프로파일러 읽기·FrameTiming 캡처 없음(그것들은 1초 주기 SamplePeriodic으로).
        /// </summary>
        public double SampleFrameWallMs()
        {
            return Time.unscaledDeltaTime * 1000.0;
        }

        /// <summary> 1초 주기 호출. 단계·구조 지표를 담아 반환. </summary>
        public PeriodicSample SamplePeriodic()
        {
            var s = new PeriodicSample();

            FrameTimingManager.CaptureFrameTimings();   // 1초 주기에만 스냅샷
            if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
            {
                ref FrameTiming t = ref _timings[0];
                s.cpuFrameMs     = t.cpuFrameTime;
                s.gpuFrameMs     = t.gpuFrameTime;
                s.mainThreadMs   = t.cpuMainThreadFrameTime;
                s.renderThreadMs = t.cpuRenderThreadFrameTime;
                s.presentWaitMs  = t.cpuMainThreadPresentWaitTime;
            }

            s.drawCalls    = Read(_drawCalls);
            s.setPassCalls = Read(_setPass);
            s.batches      = Read(_batches);
            s.triangles    = Read(_triangles);
            s.sysMemMB     = Read(_sysMem) / (1024.0 * 1024.0);
            s.gcMemMB      = Read(_gcMem)  / (1024.0 * 1024.0);

            return s;
        }

        public void Dispose()
        {
            if (!_started) return;
            _drawCalls.Dispose();
            _setPass.Dispose();
            _batches.Dispose();
            _triangles.Dispose();
            _sysMem.Dispose();
            _gcMem.Dispose();
            _started = false;
        }
    }
}
