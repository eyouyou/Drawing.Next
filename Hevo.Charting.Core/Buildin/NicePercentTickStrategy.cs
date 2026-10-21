using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Buildin
{
    // 【开发关注:否 · 预留策略】当前没有任何 schema、蓝图 JSON、Python 脚本或测试引用 NicePercentTickStrategy,
    // 业务开发写图表时不用管。保留原因:百分比副图 / 涨跌幅轴需要整齐刻度时可用。
    // 接入方式:作为 strategy 参数传给 NumericTickProvider / TickProvider
    //   (参考 FeatureCanvasExtensions 里 MirrorRatioTickStrategy 的用法);
    //   要让蓝图可选,还需另写 JSON 转换器(仿 ZoomStrategyJsonConverter)——当前蓝图不认识它们。
    // 动它之前:先补单测(现在零覆盖)。
    /// <summary>
    /// 百分比小区间专用刻度策略。step 限定在 nice fraction {1,2,5,10}，避免 0.0137 这种丑刻度。
    /// 适用于 AutoScaleStrategy.Normal / IncludeZero 的百分比域；对称域优先用 NiceSymmetricTickStrategy。
    /// </summary>
    public class NicePercentTickStrategy : ITickStrategy
    {
        private readonly double _pixelsPerTick;
        private readonly int _minTicks;
        private readonly int _maxTicks;

        public NicePercentTickStrategy(double pixelsPerTick = 50.0, int minTicks = 3, int maxTicks = 7)
        {
            _pixelsPerTick = pixelsPerTick > 0 ? pixelsPerTick : 50.0;
            _minTicks = System.Math.Max(2, minTicks);
            _maxTicks = System.Math.Max(_minTicks, maxTicks);
        }

        public IEnumerable<TickMathResult> Calculate(RealRange range, double physicalLength)
        {
            if (!range.IsValid || range.Span <= 0 || physicalLength <= 0) yield break;

            int desired = System.Math.Clamp((int)(physicalLength / _pixelsPerTick), _minTicks, _maxTicks);
            double step = NiceStepHelper.NiceStep(range.Span / desired);
            if (step <= 0) yield break;

            double start = System.Math.Ceiling(range.Min / step) * step;
            int count = (int)System.Math.Floor((range.Max - start) / step + MathTolerance.NumericEqual) + 1;
            for (int i = 0; i < count; i++)
            {
                double val = start + i * step;
                if (System.Math.Abs(val) < MathTolerance.NumericEqual) val = 0;
                yield return new TickMathResult(val, isAnchor: val == 0);
            }
        }
    }
}
