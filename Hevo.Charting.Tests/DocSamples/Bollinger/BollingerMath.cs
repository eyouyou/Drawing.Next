using System;

// 开发规范 §5 布林带 SOP —— 各种计算写法共用的纯数学。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>
    /// 布林带纯数学:不认识黑板、端口、线程,只吃 Span 写 Span,方便单测和复用。
    /// 各种接入写法(管线 Compute / ComputeFeature / 进阶的自写计算 Feature)都调这里。
    /// </summary>
    public static class BollingerMath
    {
        /// <summary>
        /// 前 period-1 根数据不足,写 NaN(LineLayer / BollingerLayer 把 NaN 当断点)。
        /// 滑动窗口维护 Σx 与 Σx²,O(n),零分配。
        /// </summary>
        public static void Compute(ReadOnlySpan<double> close, int period, double width,
            Span<double> upper, Span<double> middle, Span<double> lower)
        {
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
            double sum = 0, sumSq = 0;
            for (int i = 0; i < close.Length; i++)
            {
                double x = close[i];
                sum += x;
                sumSq += x * x;
                if (i >= period)
                {
                    double old = close[i - period];
                    sum -= old;
                    sumSq -= old * old;
                }

                if (i < period - 1)
                {
                    upper[i] = middle[i] = lower[i] = double.NaN;
                    continue;
                }

                double mean = sum / period;
                // 总体方差;浮点相消可能得到极小负数,钳到 0 防 Sqrt 出 NaN
                double variance = Math.Max(0, sumSq / period - mean * mean);
                double band = width * Math.Sqrt(variance);
                middle[i] = mean;
                upper[i] = mean + band;
                lower[i] = mean - band;
            }
        }
    }
}
