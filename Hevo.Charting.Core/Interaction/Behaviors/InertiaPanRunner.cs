using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 惯性 Pan:速度环形采样 + 宿主帧时钟(WPF 下是 CompositionTarget.Rendering)衰减帧 + 撞墙自停。
    /// 通过两个回调(applyPxDelta / readActiveRange)与外层通信,不暴露状态字段。
    /// </summary>
    internal sealed class InertiaPanRunner
    {
        // 速度采样环形缓冲容量。16 帧 ≈ 抬手前 ~270ms (60fps),足够估出稳定的初速度,再大也用不到。
        private const int RingSize = 16;
        // 单帧 dt 上限(秒)。GC 暂停 / 系统休眠回来时帧间隔可能突然变成几百 ms,
        // 这种异常帧若用真实 dt 推算,会直接把 series 弹飞数百 px。直接跳过本帧,保留 _vPxPerSec 给下一帧。
        private const double MaxFrameDeltaSec = 0.1;
        // 摩擦系数的标定基准帧率。Friction=0.92 是按 60fps 校准的"半秒衰减到 ~10%",
        // 实际 fps 可能是 75/144Hz,此处通过 Math.Pow(friction, dt * FpsBaseline) 把衰减归一到时间维度,
        // 保证不同显示器刷新率下的惯性手感一致。
        private const double FpsBaseline = 60.0;

        private readonly (long Tms, double Dx)[] _ring = new (long, double)[RingSize];
        private int _head;
        private int _count;

        private bool _running;
        private double _vPxPerSec;
        private long _lastTickMs;
        private double _friction;
        private double _minV;
        private double _windowMs;
        // K 线这类按整根吸附的横轴,一帧的位移可能不到一根:没消费掉的像素留到下一帧累加
        private double _residualPx;
        private Func<double, double>? _applyDelta;
        private Func<RealRange>? _readActive;
        private IPointerHost? _clock;
        private Action? _tickHandler;
        // 拓扑追踪用:惯性帧由宿主帧时钟回调,不在任何 Feature 作用域里,
        // 写 UserRange 时要显式进入发起惯性的 Feature 作用域,否则时而记成数据源。
        private object? _traceOwner;

        public bool IsRunning => _running;

        public void ResetSamples() { _head = 0; _count = 0; }

        public void RecordSample(double dxPx)
        {
            _ring[_head] = (NowMs(), dxPx);
            _head = (_head + 1) % RingSize;
            if (_count < RingSize) _count++;
        }

        /// <summary>抬手时启动衰减循环;速度低于阈值则不启动。可重复调 Start —— 内部先 Stop 旧循环。</summary>
        /// <param name="applyPxDelta">平移若干像素,返回真正消费的像素(按整根吸附时可能是 0)。</param>
        public void Start(InertiaPanOptions config, IPointerHost clock, Func<double, double> applyPxDelta, Func<RealRange> readActiveRange, object? traceOwner = null)
        {
            Stop();
            _traceOwner = traceOwner;
            _windowMs = config.VelocityWindowMs;
            double v = EstimateVelocity();
            if (Math.Abs(v) < config.MinVelocityPxPerSec) return;

            _running = true;
            _vPxPerSec = v;
            _residualPx = 0;
            _friction = config.Friction;
            _minV = config.MinVelocityPxPerSec;
            _applyDelta = applyPxDelta;
            _readActive = readActiveRange;
            _lastTickMs = NowMs();
            _tickHandler ??= Tick;
            _clock = clock;
            clock.AddFrameCallback(_tickHandler);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            if (_tickHandler != null) _clock?.RemoveFrameCallback(_tickHandler);
            _clock = null;
            _applyDelta = null;
            _readActive = null;
        }

        private double EstimateVelocity()
        {
            if (_count == 0) return 0;
            long now = NowMs();
            double sumDx = 0;
            long oldest = now;
            int idx = (_head - 1 + RingSize) % RingSize;
            for (int i = 0; i < _count; i++)
            {
                var (t, dx) = _ring[idx];
                if (now - t > _windowMs) break;
                sumDx += dx;
                oldest = t;
                idx = (idx - 1 + RingSize) % RingSize;
            }
            long spanMs = now - oldest;
            return spanMs > 0 ? sumDx * 1000.0 / spanMs : 0;
        }

        private void Tick()
        {
            if (!_running || _applyDelta == null || _readActive == null) { Stop(); return; }
#if DEBUG
            using var traceScope = DevTools.TopologyTracer.EnterScope(_traceOwner);
#endif

            long now = NowMs();
            double dt = (now - _lastTickMs) / 1000.0;
            _lastTickMs = now;
            if (dt <= 0 || dt > MaxFrameDeltaSec) return; // 异常帧跳过,保留 velocity

            // 撞墙检测: 比对 ActiveRange 是否真动了(被 ViewportManager Hard 钳回则视为撞墙)。
            // 只在这一帧真的写了视口时判:不到一根被吸附截成 0 不是撞墙。Start 后紧跟的那一帧 dt 常常只有一两毫秒,
            // 按撞墙停的话,甩动松手后惯性多半一帧就没了
            _residualPx += _vPxPerSec * dt;
            var before = _readActive();
            double consumed = _applyDelta(_residualPx);
            if (consumed == 0) { Decay(dt); return; }
            _residualPx -= consumed;
            var after = _readActive();
            if (after.IsValid && before.IsValid
                && Math.Abs(after.Min - before.Min) < MathTolerance.NumericEqual
                && Math.Abs(after.Max - before.Max) < MathTolerance.NumericEqual)
            {
                Stop(); return;
            }

            Decay(dt);
        }

        private void Decay(double dt)
        {
            // 按 FpsBaseline (60Hz) 等效衰减:跨显示器刷新率保持一致手感
            _vPxPerSec *= Math.Pow(_friction, dt * FpsBaseline);
            if (Math.Abs(_vPxPerSec) < _minV) Stop();
        }

        private static long NowMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
    }
}
