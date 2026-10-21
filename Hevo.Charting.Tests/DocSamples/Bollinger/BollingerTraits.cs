using System;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

// 开发规范 §5 布林带 SOP —— 步骤 A:Trait。
// 本目录是 Charting.DocSamples 工程(随测试工程编译),文档只引用这里的代码,不在 md 里贴手抄副本。
// 写法按业务侧红线(LangVersion 9):块级 namespace、显式 using、不用 record struct / required。
namespace Hevo.Charting.DocSamples.Bollinger
{
    /// <summary>
    /// 数据 Trait:本帧要画的三条轨道。只是快照,不含任何计算。
    /// 列(ReadOnlyMemory)只在本帧有效,Layer 不得把它存进字段。
    /// </summary>
    public sealed record BollingerDataTrait(
        ReadOnlyMemory<double> Upper,
        ReadOnlyMemory<double> Middle,
        ReadOnlyMemory<double> Lower) : IVisualTrait;

    /// <summary>样式 Trait:上下轨与中轨各一支笔。装配期发布一次,之后不变。</summary>
    public sealed record BollingerStyle(HevoPen BandPen, HevoPen MiddlePen) : IVisualTrait
    {
        public static BollingerStyle Create(HevoColor color, double thickness = 1.0)
            => new BollingerStyle(
                new HevoPen(new HevoSolidBrush(color), thickness),
                // 中轨用虚线和上下轨区分,4/2 是线段/间隔长度(单位 = 线宽)
                new HevoPen(new HevoSolidBrush(color), thickness, new double[] { 4, 2 }));
    }
}
