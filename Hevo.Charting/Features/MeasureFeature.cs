using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Interaction;
using Hevo.Charting.Layers;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    /// <summary>测量的一端:K 线索引(连续值)与价格。</summary>
    public readonly record struct MeasurePoint(double Index, double Value);

    /// <summary><see cref="MeasureFeature"/> 的配置。</summary>
    public sealed record MeasureOptions
    {
        /// <summary>按着它拖才测量;不按时照常平移。默认 Ctrl(框选是 Shift,不冲突)。</summary>
        public PointerModifiers Modifier { get; init; } = PointerModifiers.Control;

        /// <summary>松手时调一次(锁外)。from = 按下点,to = 松手点。</summary>
        public Action<MeasurePoint, MeasurePoint>? OnMeasured { get; init; }

        /// <summary>价差的格式(走 <see cref="FieldMeta.Format"/> / <see cref="FieldMeta.Provider"/>);null = "F2"。</summary>
        public FieldMeta? ValueMeta { get; init; }
    }

    /// <summary>
    /// 测量:按住修饰键(默认 Ctrl)在绘图区拖,画测量框和标签(根数、价差、涨跌幅),松手调 <see cref="MeasureOptions.OnMeasured"/>。
    /// 不按修饰键时这块区域不进候选,拖动照常平移。装配用 <c>i.AddMeasure(...)</c>。
    /// </summary>
    public sealed class MeasureFeature : ChartFeature
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>价格所在 Y 轴的量程。</summary>
        public DataPort<RealRange> ValueRangePort { get; init; } = null!;

        public MeasureOptions Options { get; init; } = new MeasureOptions();

        // record struct:Write 比较走 IEquatable,不装箱
        private readonly record struct DragState(bool Active, HevoPoint From, HevoPoint To);

        private readonly DataPort<DragState> _statePort = new("Measure_State");
        private readonly MeasureLayer _layer = new();
        private ChartProjection _projection;
        private PointerRegion? _region;

        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            AttachLayer(_layer);
            flow.WatchPointer(this, OnGesture);
        }

        private void OnGesture(DataBlackboard board, PointerGesture g, Action<Action> later)
        {
            switch (g.Phase)
            {
                case PointerPhase.Begin:
                case PointerPhase.Move:
                    board.Write(_statePort, new DragState(true, g.PressPosition, g.Position));
                    break;
                case PointerPhase.End:
                {
                    board.Write(_statePort, default);
                    var onMeasured = Options.OnMeasured;
                    var proj = _projection;
                    if (onMeasured == null || !proj.IsValid) break;
                    var from = new MeasurePoint(proj.ScreenToDomain(g.PressPosition.X), proj.ScreenToValue(g.PressPosition.Y));
                    var to = new MeasurePoint(proj.ScreenToDomain(g.Position.X), proj.ScreenToValue(g.Position.Y));
                    later(() => onMeasured(from, to));
                    break;
                }
                case PointerPhase.Cancel:
                    board.Write(_statePort, default);
                    break;
            }
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (proj, pc) = ctx.UseProjection(ValueRangePort);
            var (state, sc) = ctx.UsePort(_statePort);
            _projection = proj;

            // 区域只跟配置有关(整块绘图区,命中时按当时的绘图区算):发一次,之后不再换
            if (_region == null)
            {
                _region = new PointerRegion
                {
                    Shape = PointerShape.WholePlot(),
                    Accepts = PointerAccepts.Drag,
                    RequiredModifiers = Options.Modifier,
                    DragCursor = PointerCursor.Cross,
                };
                ctx.Pointer().Publish(_region);
            }

            if (!pc && !sc) return;
            if (!state.Active || !proj.IsValid)
            {
                ctx.For(_layer).PublishData(MeasureTrait.Inactive);
                return;
            }

            double fromValue = proj.ScreenToValue(state.From.Y), toValue = proj.ScreenToValue(state.To.Y);
            long bars = (long)Math.Round(proj.ScreenToDomain(state.To.X)) - (long)Math.Round(proj.ScreenToDomain(state.From.X));
            double diff = toValue - fromValue;
            var meta = Options.ValueMeta;
            string diffText = meta != null ? diff.FormatValue(meta.Format, meta.Provider) : diff.ToString("F2");
            string pct = fromValue != 0 ? (diff / Math.Abs(fromValue)).ToString("+0.00%;-0.00%;0.00%") : "--";
            string label = $"{bars} 根  {(diff >= 0 ? "+" : "")}{diffText}  {pct}";
            ctx.For(_layer).PublishData(new MeasureTrait(true, state.From, state.To, label, diff >= 0));
        }

        protected override void OnDecompose()
        {
            // 同一实例拔了再插:区域表已随卸载清掉,重装后首帧要重发
            _region = null;
            _projection = default;
            base.OnDecompose();
        }
    }
}
