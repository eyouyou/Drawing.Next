using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

// 06 号文档「K 线样式变体」引用的范例:涨空心跌实心、全实心、美国线三种常见样式,影线随涨跌色。
// 写法按业务侧红线(LangVersion 9):块级 namespace、显式 using;在 CreateSolid 的结果上用 with 改可选属性。
namespace Hevo.Charting.DocSamples.Candle
{
    public static class CandleStyleSamples
    {
        /// <summary>涨空心(只描边)、跌实心,影线随涨跌色。空心描边没单独配,沿用涨影线笔。</summary>
        public static CandleStyle HollowUp(HevoColor up, HevoColor down)
            => Solid(up, down) with { UpBody = CandleBodyKind.Hollow };

        /// <summary>涨跌都实心,影线随涨跌色。</summary>
        public static CandleStyle Solid(HevoColor up, HevoColor down)
            => CandleStyle.CreateSolid(up, down, wickColor: HevoColors.Gray) with
            {
                UpWickPen = new HevoPen(new HevoSolidBrush(up), 1.0),
                DownWickPen = new HevoPen(new HevoSolidBrush(down), 1.0),
            };

        /// <summary>美国线:最高→最低竖线,开盘横杠在左、收盘横杠在右,颜色随涨跌;横杠总宽跟实体同一规则(BodyPadding)。</summary>
        public static CandleStyle American(HevoColor up, HevoColor down)
            => Solid(up, down) with { BarKind = CandleBarKind.Ohlc };
    }
}
