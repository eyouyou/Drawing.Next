using System;
using System.Collections.Generic;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;
using Hevo.Charting.LowCode.Designer;

// 开发规范 §5 布林带 SOP —— 步骤 B 写法②(Feature 层,默认):框架自带的 ComputeFeature + C# 委托。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>
    /// 写法②:不写节点类,只写一个"列进、字典出"的方法,交给框架的 <see cref="ComputeFeature"/>。
    /// 线程、锁、输出路由都由 ComputeFeature 处理(后台线程 + Compute-then-Commit)。
    /// 同一个方法贴上 <see cref="BlueprintHandlerAttribute"/> 后,蓝图 JSON 里也能按名字引用。
    /// </summary>
    public static class BollingerHandlers
    {
        /// <summary>蓝图可用的固定参数版本(BB(20, 2))。多输出的 key 对应 ComputeFeature.Outputs 的 key。</summary>
        [BlueprintHandler("boll_20_2")]
        public static IDictionary<string, object?> Bands20(ReadOnlyMemory<double> close) => Bands(close, 20, 2.0);

        public static IDictionary<string, object?> Bands(ReadOnlyMemory<double> close, int period, double width)
        {
            int n = close.Length;
            // 多输出只能走"返回字典"的签名,每次新分配三块数组(有分配)。指标轻量 / 低频时无所谓;
            // 真成了瓶颈再考虑写法①或进阶的自写计算 Feature(见 BollingerComputeNode)
            var upper = new double[n];
            var middle = new double[n];
            var lower = new double[n];
            BollingerMath.Compute(close.Span, period, width, upper, middle, lower);
            return new Dictionary<string, object?>
            {
                ["upper"] = upper,
                ["middle"] = middle,
                ["lower"] = lower,
            };
        }
    }

    public static class BollingerComputeFeatureExtensions
    {
        /// <summary>写法②的装配入口:挂一个 <see cref="ComputeFeature"/>,三根输出按字典 key 路由。</summary>
        public static SeriesBuilder AddBollingerCompute(this SeriesBuilder builder,
            DataPort<ReadOnlyMemory<double>> source, BollingerPorts output, int period = 20, double width = 2.0)
        {
            builder.Canvas.Add(new ComputeFeature
            {
                InputPort = source,
                Outputs = new Dictionary<string, DataPort<ReadOnlyMemory<double>>>
                {
                    ["upper"] = output.Upper,
                    ["middle"] = output.Middle,
                    ["lower"] = output.Lower,
                },
                // 装配期一次性闭包,捕获参数即可;不在帧路径上
                Compute = new Func<ReadOnlyMemory<double>, IDictionary<string, object?>>(
                    close => BollingerHandlers.Bands(close, period, width)),
            });
            return builder;
        }
    }
}
