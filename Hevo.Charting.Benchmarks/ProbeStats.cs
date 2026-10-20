using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hevo.Charting.Benchmarks
{
    internal static class Stats
    {
        public static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return double.NaN;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
        }

        /// <summary>均值 ± 95% 置信区间半宽(t 分布,样本数 &lt; 2 时半宽为 NaN)。</summary>
        public static (double Mean, double HalfWidth) MeanCi95(IReadOnlyList<double> v)
        {
            if (v.Count == 0) return (double.NaN, double.NaN);
            double mean = v.Average();
            if (v.Count < 2) return (mean, double.NaN);
            double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
            return (mean, T95(v.Count - 1) * sd / Math.Sqrt(v.Count));
        }

        /// <summary>中位数 ± 95% 置信区间半宽(按 bootstrap 近似:1.58·IQR/√n,McGill 1978 的缺口箱线图公式)。</summary>
        public static (double Median, double HalfWidth) MedianCi95(IReadOnlyList<double> v)
        {
            if (v.Count == 0) return (double.NaN, double.NaN);
            double med = Percentile(v, 0.5);
            if (v.Count < 2) return (med, double.NaN);
            double iqr = Percentile(v, 0.75) - Percentile(v, 0.25);
            return (med, 1.58 * iqr / Math.Sqrt(v.Count));
        }

        // 双侧 95% t 临界值,df 1..30;更大用 1.96
        private static readonly double[] TTable =
        {
            12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
            2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
            2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
        };

        private static double T95(int df) => df <= 0 ? double.NaN : df <= TTable.Length ? TTable[df - 1] : 1.96;
    }

    /// <summary>
    /// 计数基线(CI 回归门槛)。只收确定性计数:Feature 重算/帧、图层重录/帧、绘制命令/帧 —— 不收耗时,
    /// 耗时在共享 CI 机器上噪声太大。超出基线 (1+容差) 倍即判回归;明显低于基线提示更新基线。
    /// 绘制命令数随绘图区 DIP 尺寸(刻度 / 网格条数)和 PixelsPerDip(按物理像素列 LOD)变,
    /// 所以基线同时记下写入时的测量参数,比对时按它重跑;系统 DPI 对不上就不比这一项。
    /// </summary>
    internal sealed class ProbeBaseline
    {
        public sealed record Counter(double FeaturesPerFrame, double LayersPerFrame, double DrawCmdsPerFrame);

        public sealed record MeasureSettings(int WindowWidth, int WindowHeight, int Steps, int Warmup,
            string[] Scenarios, string[] Modes, double DpiScale);

        /// <summary>写入基线时的测量参数;老基线没有这一段时为 null,比对就按命令行参数跑。</summary>
        public MeasureSettings? Settings { get; set; }

        public Dictionary<string, Counter> Entries { get; set; } = new();

        public static ProbeBaseline FromEntries(IEnumerable<RenderProbe.Entry> entries, ProbeOptions opt, double dpiScale)
        {
            var b = new ProbeBaseline
            {
                Settings = new MeasureSettings(opt.WindowWidth, opt.WindowHeight, opt.Steps, opt.Warmup,
                    opt.Scenarios, opt.Modes.Select(m => m.Key).ToArray(), dpiScale),
            };
            foreach (var e in entries.Where(e => e.Frames > 0 && e.CountersDeterministic))
                b.Entries[e.Key] = new Counter(Math.Round(e.FeaturesPerFrame, 4), Math.Round(e.LayersPerFrame, 4), Math.Round(e.DrawCmdsPerFrame, 2));
            return b;
        }

        public static ProbeBaseline Load(string path)
            => JsonSerializer.Deserialize<ProbeBaseline>(File.ReadAllText(path)) ?? new ProbeBaseline();

        // 换行固定成 LF:基线入库,Windows 上序列化出 CRLF 会让每次 --write-baseline 都是整文件 diff
        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";

        /// <summary>
        /// 比对前把测量参数换成基线写入时的:计数 key 里没有窗口尺寸,参数不一致时比出来的"回归"全是误报
        /// (例如基线用 --ci 的 960x540 写入,不带 --ci 的默认 1280x720 刻度更多,绘制命令/帧整体偏高)。
        /// </summary>
        public void ApplyTo(ProbeOptions opt)
        {
            if (Settings is not { } s) return;
            opt.WindowWidth = s.WindowWidth;
            opt.WindowHeight = s.WindowHeight;
            opt.Steps = s.Steps;
            opt.Warmup = s.Warmup;
            opt.Scenarios = RenderProbe.AllFrameScenarios.Where(x => s.Scenarios.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
            opt.Modes = RenderProbe.AllModes.Where(m => s.Modes.Contains(m.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[render-probe] 测量参数按基线:size={s.WindowWidth}x{s.WindowHeight} steps={s.Steps} warmup={s.Warmup} " +
                $"scenarios={string.Join(',', opt.Scenarios)} modes={string.Join(',', opt.Modes.Select(m => m.Key))}"));
        }

        public (bool Regressed, string Text) Check(IEnumerable<RenderProbe.Entry> current, double tolerance, double dpiScale)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### render-probe 计数回归检查");
            sb.AppendLine();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"容差 {tolerance:P0}(计数超过基线 ×(1+容差) 判回归)"));
            // 系统 DPI 不同,PixelsPerDip 变了 LOD 列数就变,绘制命令数不可比;Feature / 图层重录跟 DPI 无关照比
            bool compareDrawCmds = Settings is not { } st || double.IsNaN(dpiScale) || Math.Abs(st.DpiScale - dpiScale) < 0.01;
            if (!compareDrawCmds)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"系统 DPI 缩放 {dpiScale:P0} 与基线 {Settings!.DpiScale:P0} 不同,本次不比「绘制命令/帧」。"));
            sb.AppendLine();
            sb.AppendLine("| 组合 | 指标 | 基线 | 本次 | 结论 |");
            sb.AppendLine("|---|---|---:|---:|---|");
            bool regressed = false;
            int checkedCount = 0;
            foreach (var e in current)
            {
                if (!Entries.TryGetValue(e.Key, out var b)) continue;
                checkedCount++;
                if (e.Frames == 0)
                {
                    regressed = true;
                    sb.AppendLine($"| {e.Key} | - | - | - | ❌ 失败:{e.Error} |");
                    continue;
                }
                regressed |= Row(sb, e.Key, "Feature重算/帧", b.FeaturesPerFrame, e.FeaturesPerFrame, tolerance, 0.01);
                regressed |= Row(sb, e.Key, "图层重录/帧", b.LayersPerFrame, e.LayersPerFrame, tolerance, 0.01);
                if (compareDrawCmds)
                    regressed |= Row(sb, e.Key, "绘制命令/帧", b.DrawCmdsPerFrame, e.DrawCmdsPerFrame, tolerance, 1);
            }
            // 基线里有、本次没跑出来的组合也算失败:场景改名 / 从 --ci 预设里移掉后,门槛不能悄悄变成"少比几个"
            var ran = new HashSet<string>(current.Select(e => e.Key));
            foreach (var key in Entries.Keys.Where(k => !ran.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            {
                regressed = true;
                sb.AppendLine($"| {key} | - | - | - | ❌ 本次没有跑出这个组合(场景 / 参数改了就用 --write-baseline 更新基线) |");
            }
            if (checkedCount == 0)
            {
                regressed = true;
                sb.AppendLine("| - | - | - | - | ❌ 本次结果里没有一个组合能对上基线的 key(参数和生成基线时不一致?) |");
            }
            sb.AppendLine();
            sb.AppendLine($"共比对 {checkedCount} 个组合(上表只列出偏离基线的行)。");
            sb.AppendLine(regressed ? "结论:❌ 有计数回归" : "结论:✅ 计数未回归");
            return (regressed, sb.ToString());
        }

        private static bool Row(StringBuilder sb, string key, string name, double baseline, double actual, double tol, double slack)
        {
            bool bad = actual > baseline * (1 + tol) + slack;
            bool better = actual < baseline * (1 - tol) - slack;
            string verdict = bad ? "❌ 回归" : better ? "⬇ 低于基线(可更新基线)" : "✅";
            if (bad || better)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {key} | {name} | {baseline:F2} | {actual:F2} | {verdict} |"));
            return bad;
        }
    }
}
