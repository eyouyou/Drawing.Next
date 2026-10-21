using Hevo.Charting.Abstractions;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Core
{
    /// <summary>蜡烛图渲染数据包(冷热分离的双 layer 共用此结构)。</summary>
    /// <param name="StartIndex">本切片首根 K 线的世界索引(动态层 = 末根索引;静态层 = 0)。</param>
    /// <param name="Opens">开盘价数组。</param>
    /// <param name="Highs">最高价数组。</param>
    /// <param name="Lows">最低价数组。</param>
    /// <param name="Closes">收盘价数组。</param>
    public record CandleData(
            int StartIndex,
            ReadOnlyMemory<double> Opens,
            ReadOnlyMemory<double> Highs,
            ReadOnlyMemory<double> Lows,
            ReadOnlyMemory<double> Closes) : IVisualTrait;

    /// <summary>K 线实体画法。</summary>
    public enum CandleBodyKind
    {
        /// <summary>实心:用 <see cref="CandleStyle.UpBrush"/> / <see cref="CandleStyle.DownBrush"/> 填充。</summary>
        Filled,
        /// <summary>空心:只描实体边框、不填充;影线停在实体上下边,不穿过实体内部。</summary>
        Hollow,
    }

    /// <summary>每根 K 线画成什么。</summary>
    public enum CandleBarKind
    {
        /// <summary>蜡烛:实体 + 影线。</summary>
        Candle,
        /// <summary>美国线(OHLC 柱):最高到最低一根竖线,开盘横杠在左、收盘横杠在右,不画实体。</summary>
        Ohlc,
    }

    /// <summary>蜡烛图样式。</summary>
    /// <param name="UpBrush">阳线(收 ≥ 开)柱体填充。A 股惯例为红色。空心实体不填充,但空心描边笔缺省时用这个颜色。</param>
    /// <param name="DownBrush">阴线(收 &lt; 开)柱体填充。A 股惯例为绿色。</param>
    /// <param name="WickPen">影线画笔(<see cref="UpWickPen"/> / <see cref="DownWickPen"/> 没配时上下影线共用)。</param>
    /// <param name="BodyPadding">柱体相对单格距离的内缩比例,0.2 即两侧各留 10% 空隙。美国线横杠的总宽按同一规则取。</param>
    /// <remarks>
    /// 下面几个可选属性都不设时,画面跟只有四个构造参数时逐像素相同。
    /// 在 <see cref="CreateSolid"/> / <see cref="Default"/> 的结果上用 <c>with</c> 改,例如 <c>style with { UpBody = CandleBodyKind.Hollow }</c>。
    /// </remarks>
    public record CandleStyle(
        IHevoBrush UpBrush,
        IHevoBrush DownBrush,
        HevoPen WickPen,
        double BodyPadding = 0.2
    ) : IVisualTrait
    {
        /// <summary>阳线影线画笔;null = 用 <see cref="WickPen"/>。美国线的竖线和横杠也用它。</summary>
        public HevoPen? UpWickPen { get; init; }

        /// <summary>阴线影线画笔;null = 用 <see cref="WickPen"/>。</summary>
        public HevoPen? DownWickPen { get; init; }

        /// <summary>阳线实体画法,默认实心。</summary>
        public CandleBodyKind UpBody { get; init; }

        /// <summary>阴线实体画法,默认实心。</summary>
        public CandleBodyKind DownBody { get; init; }

        /// <summary>阳线空心实体的描边笔;null = <see cref="UpWickPen"/>,也没配就是 <see cref="UpBrush"/> 颜色的 1px 线。</summary>
        public HevoPen? UpOutlinePen { get; init; }

        /// <summary>阴线空心实体的描边笔;null = <see cref="DownWickPen"/>,也没配就是 <see cref="DownBrush"/> 颜色的 1px 线。</summary>
        public HevoPen? DownOutlinePen { get; init; }

        /// <summary>蜡烛还是美国线,默认蜡烛。</summary>
        public CandleBarKind BarKind { get; init; }

        public static CandleStyle CreateSolid(
            HevoColor upColor,
            HevoColor downColor,
            HevoColor wickColor,
            double wickWidth = 1.0,
            double bodyPadding = 0.2)
        {
            return new CandleStyle(
                UpBrush: new HevoSolidBrush(upColor),
                DownBrush: new HevoSolidBrush(downColor),
                WickPen: new HevoPen(new HevoSolidBrush(wickColor), wickWidth),
                BodyPadding: bodyPadding
            );
        }

        public static readonly CandleStyle Default = CreateSolid(
            upColor: HevoColor.FromRgb(234, 71, 109),
            downColor: HevoColor.FromRgb(38, 166, 154),
            wickColor: HevoColor.FromRgb(128, 128, 128)
        );

        // 只用四个构造参数的老样式:LOD 时保留"影线 + 1px 实体"的老画法,画面不变
        internal bool IsClassic => UpWickPen == null && DownWickPen == null
            && UpBody == CandleBodyKind.Filled && DownBody == CandleBodyKind.Filled && BarKind == CandleBarKind.Candle;
    }

    public partial class CandleLayer : ChartLayer
    {
        // 跨帧复用,首次 OnUpdate 后 capacity 锁定在水位线,后续帧 0 分配。
        // 512 = wick 走 LineSegments 每根 K 线 2 个端点。
        // 不暴露成配置:作用纯粹是"避免首次扩容拷贝",业务调小反加重 GC,调大也不省内存。
        private readonly List<HevoPoint> _wicks = new(512); // 涨跌共用 WickPen 时的影线
        private readonly Side _up = new(), _down = new();
        private double _minBodyHeight = 1.0; // DIP,每帧按 PixelsPerDip 重算
        private double _k = 1.0;
        private bool _ohlc;

        /// <summary>
        /// 涨或跌一侧:本帧解析出的画法 + 跨帧复用的批量列表。一条绘制指令只配一支笔 / 一把刷,
        /// 所以每种颜色组合一个列表;默认样式只用到 <see cref="Filled"/> 和共用的影线列表,其余一直是空的。
        /// </summary>
        private sealed class Side
        {
            // 256 ≈ 单屏典型 K 线根数(120~250)的常态上限
            public readonly List<HevoRect> Filled = new(256);
            public readonly List<HevoPoint> OwnWicks = new();
            public readonly List<HevoRect> Hollow = new();
            // 空心实体扁到放不下内部(十字星、极窄)时,描边会叠成一块,直接用描边色填
            public readonly List<HevoRect> OutlineFill = new();

            public List<HevoPoint> Wicks = null!; // OwnWicks,或没配本侧影线笔时的共用列表
            public HevoPen WickPen = null!, Outline = null!;
            public IHevoBrush Fill = null!;
            public bool HollowBody;
            public int LinePx, OutlinePx;
            private HevoPen? _outlineFallback; // 按画刷缓存,换样式才重建

            public void Resolve(IHevoBrush fill, HevoPen? wickPen, HevoPen? outlinePen, CandleBodyKind body,
                                CandleStyle style, List<HevoPoint> sharedWicks, bool ohlc, double k)
            {
                Filled.Clear(); OwnWicks.Clear(); Hollow.Clear(); OutlineFill.Clear();
                Fill = fill;
                WickPen = wickPen ?? style.WickPen;
                Wicks = wickPen != null ? OwnWicks : sharedWicks;
                LinePx = ColumnWidth.LinePixels(WickPen, k);
                HollowBody = !ohlc && body == CandleBodyKind.Hollow;
                // 描边笔缺省:本侧影线笔,再没有就是本侧颜色的 1px 线
                if (outlinePen == null && wickPen == null && (_outlineFallback == null || !ReferenceEquals(_outlineFallback.Brush, fill)))
                    _outlineFallback = new HevoPen(fill, 1.0);
                Outline = outlinePen ?? wickPen ?? _outlineFallback!;
                OutlinePx = ColumnWidth.LinePixels(Outline, k);
            }

            // LOD 竖线的颜色 = 正常大小时这根 K 线最显眼的那部分:美国线是线色,空心是描边色,实心是实体色
            public IHevoBrush LodBrush(bool ohlc) => ohlc ? WickPen.Brush : HollowBody ? Outline.Brush : Fill;
        }

        public CandleLayer()
        {
            Name = "CandleStick";
            Mode = RenderMode.Software;
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var candleData = data.Get<CandleData>();
            var plotAreaTrait = data.Get<PlotAreaTrait>();
            var xAxisTrait = data.Get<XAxisTrait>();
            var yAxisTrait = data.Get<YAxisTrait>();

            // 默认兜底，但绝不会干扰其数学计算纯洁性
            var axis = data.Get<ScaleStrategyTrait>();
            var style = data.Get<CandleStyle>() ?? CandleStyle.Default;

            if (candleData == null || plotAreaTrait == null || xAxisTrait == null || yAxisTrait == null || axis == null) return;

            HevoRect plotArea = plotAreaTrait.Area;
            RealRange xRange = xAxisTrait.Viewport;
            RealRange yRange = yAxisTrait.Viewport;

            if (!xRange.IsValid || !yRange.IsValid || plotArea.Width <= 0 || plotArea.Height <= 0) return;

            var opens = candleData.Opens.Span;
            var highs = candleData.Highs.Span;
            var lows = candleData.Lows.Span;
            var closes = candleData.Closes.Span;

            int dataLength = opens.Length;
            if (dataLength == 0) return;

            int baseIndex = candleData.StartIndex;

            // 💥 纯 UI 层的平滑视口剔除 (Culling)，两端各缓冲 2 根，防止拖拽闪烁
            int viewStart = (int)Math.Floor(xRange.Min) - 2;
            int viewEnd = (int)Math.Ceiling(xRange.Max) + 2;

            int logicalStart = Math.Max(viewStart, baseIndex);
            int logicalEnd = Math.Min(viewEnd, baseIndex + dataLength - 1);

            if (logicalStart > logicalEnd) return;

            // ==========================================
            // 💥 极致多态：每根的格宽都问 scale(ProjectDomainSlotWidth),不等间距的 scale 也成立
            // ==========================================
            // LOD 判定看可见区起点那一格的密度
            double ppuX = CoordinateExtensions.ProjectDomainSlotWidth(plotArea, xRange, axis, logicalStart);

            // 扣除业务所需的间隙留白 (BodyPadding)。宽度取整数设备像素且跟影线同奇偶:
            // 渲染器按像素中心规则排,整数宽每根都一样宽,影线(同一个中心上的竖线)落在实体正中。
            // 涨跌影线笔线宽不同时各按各的奇偶取宽
            double pixelsPerDip = PixelsPerDip;
            _k = pixelsPerDip;
            _ohlc = style.BarKind == CandleBarKind.Ohlc;
            _wicks.Clear();
            _up.Resolve(style.UpBrush, style.UpWickPen, style.UpOutlinePen, style.UpBody, style, _wicks, _ohlc, pixelsPerDip);
            _down.Resolve(style.DownBrush, style.DownWickPen, style.DownOutlinePen, style.DownBody, style, _wicks, _ohlc, pixelsPerDip);
            double bodyRatio = 1.0 - style.BodyPadding;
            // 十字星(开 = 收)的最小高度:1 个设备像素,不随亚像素位置在 1 / 2 行之间跳
            _minBodyHeight = ColumnWidth.MinHeight(pixelsPerDip) / pixelsPerDip;

            IHevoBrush upFill = style.UpBrush, downFill = style.DownBrush;

            // 💥 LOD:单根不足 1 个物理像素宽时(缩放到几千上万根可见),逐根画只是在同一列像素上反复覆盖,
            // 却让 WPF 为每根建影线 figure + DrawRectangle 记录,2 万根可见时每帧上 MB 的 LOH 分配。
            // 这时按物理像素列聚合(首开、最高、最低、末收,涨跌色按聚合后的开收),每列一根影线 + 一个 1px 实体,
            // 输出上限 = 绘图区物理宽度。聚合只取决于本帧可见区和像素宽度,增量路径(Tick / Append)照常整层重录即可。
            // 样式变体(空心 / 美国线 / 分涨跌影线)在 1px 宽里分不出来,每列只画一根最高到最低的竖线,颜色取该样式的涨 / 跌主色
            if (PixelColumnLod.ShouldAggregate(ppuX, pixelsPerDip))
            {
                bool classic = style.IsClassic;
                if (!classic)
                {
                    upFill = _up.LodBrush(_ohlc);
                    downFill = _down.LodBrush(_ohlc);
                }
                double columnHalf = 0.5 / pixelsPerDip;
                var acc = new OhlcColumnAccumulator();
                OhlcColumn done;
                for (int logicalIndex = logicalStart; logicalIndex <= logicalEnd; logicalIndex++)
                {
                    int arrayIndex = logicalIndex - baseIndex;
                    double o = opens[arrayIndex];
                    if (IsMissing(o, highs[arrayIndex], lows[arrayIndex], closes[arrayIndex])) continue;

                    double xCenter = CoordinateExtensions.ProjectDomainToScreen(plotArea, xRange, axis, logicalIndex);
                    int column = PixelColumnLod.ColumnOf(xCenter, pixelsPerDip);
                    // 整列都在绘图区内才画,跟逐根路径"柱体越界即剔除"同口径
                    if (!PixelColumnLod.ColumnInside(column, pixelsPerDip, plotArea.Left, plotArea.Right)) continue;

                    if (acc.Add(column, o, highs[arrayIndex], lows[arrayIndex], closes[arrayIndex], out done))
                    {
                        AddLodColumn(classic, done, columnHalf, plotArea, yRange, axis);
                    }
                }
                if (acc.Flush(out done))
                {
                    AddLodColumn(classic, done, columnHalf, plotArea, yRange, axis);
                }
            }
            else
            {
                for (int logicalIndex = logicalStart; logicalIndex <= logicalEnd; logicalIndex++)
                {
                    int arrayIndex = logicalIndex - baseIndex;

                    double o = opens[arrayIndex];
                    if (IsMissing(o, highs[arrayIndex], lows[arrayIndex], closes[arrayIndex])) continue;

                    // 💥 绝对中心点：Scale 去操心居中偏移，Layer 只管画！(十字线走同一个投影)
                    double xCenter = CoordinateExtensions.ProjectDomainToScreen(plotArea, xRange, axis, logicalIndex);
                    double slot = CoordinateExtensions.ProjectDomainSlotWidth(plotArea, xRange, axis, logicalIndex);
                    int linePx = (closes[arrayIndex] >= o ? _up : _down).LinePx;
                    int bodyPx = ColumnWidth.Of(slot, bodyRatio, pixelsPerDip, linePx);
                    double halfBody = bodyPx / (2.0 * pixelsPerDip);

                    // 边界剔除
                    if (xCenter - halfBody < plotArea.Left || xCenter + halfBody > plotArea.Right) continue;

                    AddCandle(xCenter, halfBody, bodyPx, o, highs[arrayIndex], lows[arrayIndex], closes[arrayIndex], plotArea, yRange, axis);
                }
            }

            using (draw.PushClip(plotArea))
            {
                if (_wicks.Count > 0)
                {
                    draw.DrawLineSegments(style.WickPen, _wicks);
                }
                if (_up.OwnWicks.Count > 0) draw.DrawLineSegments(_up.WickPen, _up.OwnWicks);
                if (_down.OwnWicks.Count > 0) draw.DrawLineSegments(_down.WickPen, _down.OwnWicks);

                if (_up.Filled.Count > 0) draw.DrawRectangles(upFill, null, _up.Filled);
                if (_down.Filled.Count > 0) draw.DrawRectangles(downFill, null, _down.Filled);

                // 空心实体交给渲染器的"内描边":外框跟同位置的实心实体取到同一组设备像素,影线照旧在正中
                if (_up.OutlineFill.Count > 0) draw.DrawRectangles(_up.Outline.Brush, null, _up.OutlineFill);
                if (_down.OutlineFill.Count > 0) draw.DrawRectangles(_down.Outline.Brush, null, _down.OutlineFill);
                if (_up.Hollow.Count > 0) draw.DrawRectangles(null, _up.Outline, _up.Hollow);
                if (_down.Hollow.Count > 0) draw.DrawRectangles(null, _down.Outline, _down.Hollow);
            }
        }

        /// <summary>
        /// 缺失的 K 线 = OHLC 任一为 NaN / ±∞。以前用 <c>open &lt;= 0</c> 挡 ScatterIngestor 给没映射到的格子填的 0 占位,
        /// 但期货价差合约、2020 年原油的负价 / 零价是真实行情,不能当缺失。缺失值约定为 NaN:
        /// 稀疏映射的 K 线列要给 Map 传 <c>defaultValue: double.NaN</c>。
        /// </summary>
        internal static bool IsMissing(double o, double h, double l, double c)
            => !double.IsFinite(o) || !double.IsFinite(h) || !double.IsFinite(l) || !double.IsFinite(c);

        private void AddLodColumn(bool classic, OhlcColumn col, double columnHalf, HevoRect plotArea, RealRange yRange, ScaleStrategyTrait axis)
        {
            // 影线和 1px 实体都在列中心,渲染器按同一条像素中心规则落到这一列
            double xCenter = PixelColumnLod.ColumnCenter(col.Column, _k);
            if (classic)
            {
                AddCandle(xCenter, columnHalf, 1, col.Open, col.High, col.Low, col.Close, plotArea, yRange, axis);
                return;
            }
            // 1px 宽、最高到最低的填充矩形:跟老画法的 1px 实体同一套取整,落在同一列
            double yHigh = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, col.High);
            double yLow = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, col.Low);
            double height = Math.Max(_minBodyHeight, yLow - yHigh);
            var rect = new HevoRect((float)(xCenter - columnHalf), (float)yHigh, (float)(2 * columnHalf), (float)height);
            (col.Close >= col.Open ? _up : _down).Filled.Add(rect);
        }

        // xCenter:影线与实体共用的中心(交给渲染器按像素中心规则落列);halfBody:实体半宽;bodyPx:实体宽(设备像素)
        private void AddCandle(double xCenter, double halfBody, int bodyPx, double o, double h, double l, double c,
                               HevoRect plotArea, RealRange yRange, ScaleStrategyTrait axis)
        {
            double yOpen = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, o);
            double yHigh = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, h);
            double yLow = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, l);
            double yClose = CoordinateExtensions.ProjectValueToScreen(plotArea, yRange, axis, c);

            var side = c >= o ? _up : _down;
            var wicks = side.Wicks;
            float x = (float)xCenter;

            if (_ohlc)
            {
                wicks.Add(new HevoPoint(x, (float)yHigh));
                wicks.Add(new HevoPoint(x, (float)yLow));
                // 横杠从实体边缘画到竖线外沿,不跟竖线重叠(半透明颜色不会叠深);整数宽 + 同奇偶时两侧横杠等长
                double inner = side.LinePx / (2.0 * _k);
                if (halfBody > inner)
                {
                    wicks.Add(new HevoPoint((float)(xCenter - halfBody), (float)yOpen));
                    wicks.Add(new HevoPoint((float)(xCenter - inner), (float)yOpen));
                    wicks.Add(new HevoPoint((float)(xCenter + inner), (float)yClose));
                    wicks.Add(new HevoPoint((float)(xCenter + halfBody), (float)yClose));
                }
                return;
            }

            double top = Math.Min(yOpen, yClose);
            double bottom = Math.Max(yOpen, yClose);
            double height = Math.Max(_minBodyHeight, bottom - top);

            double leftEdge = xCenter - halfBody;
            double rightEdge = xCenter + halfBody;
            HevoRect rect = new HevoRect((float)leftEdge, (float)top, (float)(rightEdge - leftEdge), (float)height);

            if (!side.HollowBody)
            {
                wicks.Add(new HevoPoint(x, (float)yHigh));
                wicks.Add(new HevoPoint(x, (float)yLow));
                side.Filled.Add(rect);
                return;
            }

            // 空心:上下影线分两段,端点就是实体矩形的上下边。线端和矩形边走同一条取整,影线正好接在描边外沿
            if ((float)yHigh < rect.Y)
            {
                wicks.Add(new HevoPoint(x, (float)yHigh));
                wicks.Add(new HevoPoint(x, rect.Y));
            }
            if ((float)yLow > rect.Bottom)
            {
                wicks.Add(new HevoPoint(x, rect.Bottom));
                wicks.Add(new HevoPoint(x, (float)yLow));
            }
            bool noInterior = bodyPx <= 2 * side.OutlinePx || height * _k < 2 * side.OutlinePx + 1;
            (noInterior ? side.OutlineFill : side.Hollow).Add(rect);
        }
    }
}
