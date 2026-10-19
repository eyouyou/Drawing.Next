using System.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Tests
{
    /// <summary>惯性执行器:按整根吸附的横轴上,一帧不到一根的位移不能当成撞墙。</summary>
    public sealed class InertiaPanRunnerTests
    {
        /// <summary>
        /// Start 后紧跟的那一帧 dt 只有一两毫秒,位移不到一根被吸附截成 0。
        /// 这一帧不能按撞墙停(真实输入下甩动松手后惯性多半一帧就停,见 interaction-probe 场景 2),残量留到后面的帧。
        /// </summary>
        [Fact]
        public void SubQuantumFirstTick_DoesNotStop_ResidualCarriesOver()
        {
            const double Quantum = 200;  // 一根 = 200px:Sleep(1) 实际可能睡到 15ms,首帧仍不到一根
            double consumedTotal = 0;
            var clock = new ManualClock();
            var runner = new InertiaPanRunner();
            for (int i = 0; i < 4; i++) { runner.RecordSample(40); Thread.Sleep(10); }

            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock,
                applyPxDelta: dx =>
                {
                    double snapped = System.Math.Truncate(dx / Quantum) * Quantum;
                    consumedTotal += snapped;
                    return snapped;
                },
                readActiveRange: () => new RealRange(-consumedTotal / Quantum, 100 - consumedTotal / Quantum));

            Thread.Sleep(1);
            clock.Tick();   // dt ≈ 1ms:不到一根
            bool runningAfterTinyTick = runner.IsRunning;
            double afterTinyTick = consumedTotal;
            for (int i = 0; i < 10 && runner.IsRunning; i++) { Thread.Sleep(16); clock.Tick(); }
            runner.Stop();

            Assert.True(runningAfterTinyTick);
            Assert.Equal(0, afterTinyTick);
            Assert.True(consumedTotal >= Quantum, $"惯性应当移动至少一根,实际 {consumedTotal}px");
        }

        /// <summary>真的撞墙(写了视口但被钳回不动)照常停。</summary>
        [Fact]
        public void ConsumedButRangeUnchanged_StopsAtWall()
        {
            var clock = new ManualClock();
            var runner = new InertiaPanRunner();
            for (int i = 0; i < 4; i++) { runner.RecordSample(40); Thread.Sleep(10); }
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock,
                applyPxDelta: dx => dx,
                readActiveRange: () => new RealRange(0, 100));
            Thread.Sleep(16);
            clock.Tick();

            Assert.False(runner.IsRunning);
        }

        /// <summary>只提供帧回调的宿主;其余能力惯性用不到。</summary>
        private sealed class ManualClock : IPointerHost
        {
            private Action? _callback;
            public void Tick() => _callback?.Invoke();
            public void AddFrameCallback(Action callback) => _callback = callback;
            public void RemoveFrameCallback(Action callback) { if (_callback == callback) _callback = null; }

            public bool HasPendingFrame => false;
            public void RunPendingFrame() { }
            public void Focus() { }
            public void Capture() { }
            public void ReleaseCapture() { }
            public void HandOverCapture() { }
            public bool IsMouseCapturedByOther => false;
            public void SetCursor(PointerCursor cursor) { }
            public HevoRect PlotArea => HevoRect.Empty;
            public DataBlackboard? Board => null;
            public double DragThreshold => 4;
            public void CaptureForBackground() { }
            public void ReleaseBackgroundCapture() { }
            public int BackgroundCaptureCount => 0;
        }
    }
}
