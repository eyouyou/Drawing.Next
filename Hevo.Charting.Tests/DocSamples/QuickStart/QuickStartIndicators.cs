using System;
using System.Collections.Generic;
using Hevo.Charting.LowCode.Designer;

// 15 分钟上手 L2:用一个 C# 类实现指标。
// 照抄到 Drawing.LowCodeDemo 里即可用(写法按 Demo 工程的 LangVersion 9)。
namespace Hevo.Charting.DocSamples.QuickStart
{
    /// <summary>
    /// 一个类放两个指标,蓝图里按 <c>[BlueprintHandler("名字")]</c> 的名字引用:
    /// <list type="bullet">
    ///   <item><c>qs_sma20</c>:单线指标,交给 <c>ComputeFeature</c>(算出一根列,再用 <c>LineSeriesFeature</c> 画)。</item>
    ///   <item><c>qs_channel</c>:多线指标,交给 <c>PlotFeature</c>(返回字典,一个节点自己画多条线)。</item>
    /// </list>
    /// 注册:<c>registry.AutoDiscoverStatic(typeof(QuickStartIndicators))</c>。
    /// </summary>
    public static class QuickStartIndicators
    {
        public const int Length = 20;

        /// <summary>
        /// 20 日均线。零分配签名:框架给好 <paramref name="result"/>(长度 = 输入长度),每个元素都要写。
        /// 形参名 <c>close</c> 就是蓝图里这个输入的名字。
        /// </summary>
        [BlueprintHandler("qs_sma20")]
        public static void Sma20(ReadOnlySpan<double> close, Span<double> result)
        {
            double sum = 0;
            for (int i = 0; i < close.Length; i++)
            {
                sum += close[i];
                if (i >= Length) sum -= close[i - Length];
                // 不足 20 根写 NaN,画线时自动断开
                result[i] = i >= Length - 1 ? sum / Length : double.NaN;
            }
        }

        /// <summary>
        /// 20 日价格通道:最高收盘 / 最低收盘 / 二者中点。
        /// 返回字典,key 对应蓝图 PlotFeature 的 <c>Series[].Name</c>;值是 double[] 或 ReadOnlyMemory&lt;double&gt;。
        /// </summary>
        [BlueprintHandler("qs_channel")]
        public static object Channel20(ReadOnlyMemory<double> close)
        {
            var span = close.Span;
            int n = span.Length;
            var upper = new double[n];
            var middle = new double[n];
            var lower = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (i < Length - 1)
                {
                    upper[i] = middle[i] = lower[i] = double.NaN;
                    continue;
                }
                double hi = double.MinValue, lo = double.MaxValue;
                for (int j = i - Length + 1; j <= i; j++)
                {
                    if (span[j] > hi) hi = span[j];
                    if (span[j] < lo) lo = span[j];
                }
                upper[i] = hi;
                lower[i] = lo;
                middle[i] = (hi + lo) / 2;
            }
            return new Dictionary<string, object?>
            {
                ["upper"] = upper,
                ["middle"] = middle,
                ["lower"] = lower,
            };
        }
    }
}
