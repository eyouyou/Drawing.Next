using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Rig = Hevo.Charting.Tests.PointerRouterTests.Rig;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 惯性:执行器本身(速度门槛、摩擦衰减到停、异常长帧跳过、停了再起)用手动时钟逐帧驱动;
    /// 跟图表的配合(滚轮 / 双击复原停惯性、关掉 InertiaPan、配置透传)走真实输入。
    /// 有真实输入的用例,整类独占运行。
    /// </summary>
    [Collection(nameof(RealMouseInputCollection))]
    public sealed class InertiaBehaviorTests
    {
        private static readonly FieldInfo VelocityField =
            typeof(InertiaPanRunner).GetField("_vPxPerSec", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static double Velocity(InertiaPanRunner r) => (double)VelocityField.GetValue(r)!;

        private static void Fling(InertiaPanRunner runner, double dxPerSample, int samples = 4)
        {
            for (int i = 0; i < samples; i++) { runner.RecordSample(dxPerSample); Thread.Sleep(10); }
        }

        /// <summary>平移累计量;读视口时按累计量挪,不会被当成撞墙。</summary>
        private sealed class FreeTrack
        {
            public double Total;
            public int Calls;
            public double Apply(double dx) { Total += dx; Calls++; return dx; }
            public RealRange Read() => new RealRange(-Total, 100 - Total);
        }

        // ================= 执行器 =================

        [Fact]
        public void SlowRelease_BelowMinVelocity_DoesNotStart()
        {
            var clock = new ManualClock();
            var runner = new InertiaPanRunner();
            var track = new FreeTrack();
            Fling(runner, 0.01);   // 约 1px/s,远低于默认 30px/s
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock, track.Apply, track.Read);

            Assert.False(runner.IsRunning);
            Assert.Equal(0, clock.CallbackCount);
        }

        [Fact]
        public void NoSamples_OrSamplesOlderThanWindow_DoesNotStart()
        {
            var clock = new ManualClock();
            var runner = new InertiaPanRunner();
            var track = new FreeTrack();
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock, track.Apply, track.Read);
            bool startedWithoutSamples = runner.IsRunning;

            Fling(runner, 40);
            Thread.Sleep(120);   // 抬手前停住:采样都过了窗口,等于没甩
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 50 }, clock, track.Apply, track.Read);

            Assert.False(startedWithoutSamples);
            Assert.False(runner.IsRunning);
        }

        [Fact]
        public void Friction_DecaysVelocity_UntilStop()
        {
            var clock = new ManualClock();
            var runner = new InertiaPanRunner();
            var track = new FreeTrack();
            Fling(runner, 40);
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock, track.Apply, track.Read);
            double v0 = Velocity(runner);
            Assert.True(runner.IsRunning);

            var speeds = new List<double> { v0 };
            for (int i = 0; i < 400 && runner.IsRunning; i++)
            {
                Thread.Sleep(16);
                clock.Tick();
                speeds.Add(Math.Abs(Velocity(runner)));
            }

            Assert.False(runner.IsRunning, $"400 帧后仍在惯性,速度 {speeds.Last():F1}");
            Assert.Equal(0, clock.CallbackCount);
            // 异常长帧会原样保留速度,所以只要求不增
            for (int i = 1; i < speeds.Count; i++)
                Assert.True(speeds[i] <= speeds[i - 1] + 1e-9, $"第 {i} 帧速度回升:{speeds[i - 1]:F2} → {speeds[i]:F2}");
            Assert.True(speeds.Last() < 30, "停在 MinVelocity 以下");
            Assert.True(track.Total > 0, "惯性沿甩动方向走");
        }

        [Fact]
        public void LongFrame_IsSkipped_VelocityKept_NextFrameMoves()
        {
            var clock = new ManualClock();
            var runner = new InertiaPanRunner();
            var track = new FreeTrack();
            Fling(runner, 40);
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock, track.Apply, track.Read);
            double v0 = Velocity(runner);

            Thread.Sleep(150);   // 超过 0.1s 的帧:GC / 休眠回来
            clock.Tick();
            int callsAfterLong = track.Calls;
            double vAfterLong = Velocity(runner);
            bool runningAfterLong = runner.IsRunning;

            Thread.Sleep(16);
            clock.Tick();
            runner.Stop();

            Assert.Equal(0, callsAfterLong);
            Assert.Equal(v0, vAfterLong);
            Assert.True(runningAfterLong);
            Assert.Equal(1, track.Calls);
            Assert.True(track.Total > 0);
        }

        [Fact]
        public void Stop_UnhooksClock_ThenRestart_Runs()
        {
            var clock = new ManualClock();
            var runner = new InertiaPanRunner();
            var track = new FreeTrack();
            Fling(runner, 40);
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock, track.Apply, track.Read);
            runner.Stop();
            bool runningAfterStop = runner.IsRunning;
            int hooksAfterStop = clock.CallbackCount;
            Thread.Sleep(16);
            clock.Tick();
            int callsWhileStopped = track.Calls;

            runner.ResetSamples();
            Fling(runner, -40);
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock, track.Apply, track.Read);
            // 重复 Start 先停旧循环:时钟上始终只挂一个回调
            runner.Start(new InertiaPanOptions { VelocityWindowMs = 200 }, clock, track.Apply, track.Read);
            int hooksAfterRestart = clock.CallbackCount;
            Thread.Sleep(16);
            clock.Tick();
            runner.Stop();

            Assert.False(runningAfterStop);
            Assert.Equal(0, hooksAfterStop);
            Assert.Equal(0, callsWhileStopped);
            Assert.Equal(1, hooksAfterRestart);
            Assert.True(track.Total < 0, "重新甩向左,惯性向左");
        }

        // ================= 跟图表的配合(真实输入) =================

        private static Rig ModeRig(ChartInteractionMode modes, InertiaPanOptions? inertia = null)
            => new Rig(extra: (i, s) => i.Remove<ChartInteractionFeature>().EnableStandardPointer(s.Hit, modes,
                inertiaPanConfig: inertia ?? new InertiaPanOptions { VelocityWindowMs = 2000 }));

        [Fact]
        public void Wheel_StopsInertia() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                rig.FlingRight();
                bool runningAfterFling = rig.InertiaRunning;
                rig.Mouse.Wheel(rig.Canvas, new Point(200, 150), 120);
                rig.Frame();
                Assert.True(runningAfterFling);
                Assert.False(rig.InertiaRunning);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void DoubleClickReset_StopsInertia_AndRestoresViewport() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                rig.FlingRight();
                bool runningAfterFling = rig.InertiaRunning;
                var flung = rig.ActiveRange;
                rig.Click(300, 100);
                rig.Click(300, 100);
                Assert.True(runningAfterFling);
                Assert.NotEqual(rig.Initial.Min, flung.Min);
                Assert.False(rig.InertiaRunning);
                Assert.Equal(rig.Initial.Min, rig.ActiveRange.Min, 6);
                Assert.Equal(rig.Initial.Max, rig.ActiveRange.Max, 6);
            }
            finally { rig.Close(); }
        });

        [Fact]
        public void InertiaPanModeOff_FlingPansButNoInertia() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = ModeRig(ChartInteractionMode.All & ~ChartInteractionMode.InertiaPan);
            try
            {
                rig.FlingRight();
                Assert.True(rig.ActiveRange.Min < rig.Initial.Min);
                Assert.False(rig.InertiaRunning);
            }
            finally { rig.Close(); }
        });

        /// <summary>配置透传:门槛调到够不着,同样的甩动不起惯性。</summary>
        [Fact]
        public void MinVelocityOption_ReachesRunner() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = ModeRig(ChartInteractionMode.All, new InertiaPanOptions { VelocityWindowMs = 2000, MinVelocityPxPerSec = 1e7 });
            try
            {
                rig.FlingRight();
                Assert.True(rig.ActiveRange.Min < rig.Initial.Min);
                Assert.False(rig.InertiaRunning);
            }
            finally { rig.Close(); }
        });

        /// <summary>帧时钟真的跑起来(CompositionTarget.Rendering):松手后视口继续往同一方向走,然后自己停。</summary>
        [Fact]
        public void Fling_KeepsPanning_OnRenderFrames_ThenStops() => IncrementalTestKit.RunOnSta(() =>
        {
            var rig = new Rig();
            try
            {
                rig.FlingRight();
                var released = rig.ActiveRange;
                Assert.True(rig.InertiaRunning);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (rig.InertiaRunning && DateTime.UtcNow < deadline) Pump(50);
                rig.Frame();
                Assert.False(rig.InertiaRunning, "5 秒内惯性应停");
                Assert.True(rig.ActiveRange.Min < released.Min, $"{released} → {rig.ActiveRange}");
            }
            finally { rig.Close(); }
        });

        private static void Pump(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>只提供帧回调的宿主;记下挂了几个回调。</summary>
        private sealed class ManualClock : IPointerHost
        {
            private readonly List<Action> _callbacks = new();
            public int CallbackCount => _callbacks.Count;
            public void Tick() { foreach (var c in _callbacks.ToArray()) c(); }
            public void AddFrameCallback(Action callback) { if (!_callbacks.Contains(callback)) _callbacks.Add(callback); }
            public void RemoveFrameCallback(Action callback) => _callbacks.Remove(callback);

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
