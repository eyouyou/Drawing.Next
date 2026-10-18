using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Windows.Media;

namespace Hevo.Charting.Features
{
    public record CandleMetas(FieldMeta? Open = null, FieldMeta? High = null, FieldMeta? Low = null, FieldMeta? Close = null);
    public static class CandleFeatureExtensions
    {
        // --- 🕯️ 3. Series ---
        // Viewport 由 ChartFeature.InternalCompose 从 ViewportManagerFeature.Ports 自动注入（L6 / §B.2.6），外部不再显式传 vp。
        public static SeriesBuilder AddCandle(
        this SeriesBuilder builder,
        DataPort<RealRange> rangePort,
        CandlePorts ports,
        string groupName = "Candle",
        CandleStyle? style = null,
        CandleMetas? metas = null)
        {
            var feature = new CandleFeature
            {
                YRangePort = rangePort,
                Ports = ports,
                GroupName = groupName,
                Style = style ?? CandleStyle.Default
            };

            if (metas != null)
            {
                if (metas.Open is FieldMeta open) feature.OpenMeta = open;
                if (metas.High is FieldMeta high) feature.HighMeta = high;
                if (metas.Low is FieldMeta low) feature.LowMeta = low;
                if (metas.Close is FieldMeta close) feature.CloseMeta = close;
            }

            builder.Canvas.Add(feature);
            return builder;
        }
    }

    /// <summary>
    /// 💥 蜡烛图专属引脚全家桶 (一次实例化，带走 5 个引脚)
    /// </summary>
    public record CandlePorts(
        DataPort<ReadOnlyMemory<DateTime>> Time,
        DataPort<ReadOnlyMemory<double>> Open,
        DataPort<ReadOnlyMemory<double>> High,
        DataPort<ReadOnlyMemory<double>> Low,
        DataPort<ReadOnlyMemory<double>> Close
    )
    {
        // 极简构造函数，外部直接 new CandlePorts() 即可
        public CandlePorts() : this(new("Candle_Time"), new("Candle_Open"), new("Candle_High"), new("Candle_Low"), new("Candle_Close")) { }
    }

    /// <summary>
    /// 💥 视口驱动的极致 K 线（蜡烛图）渲染层
    /// [架构定位]：图表引擎的视觉终端。
    /// [核心职责]：消费全量 OHLC 列(世界索引 == 数组下标),按冷热分离录两层:
    /// 静态层画历史段 [0, len-1),动态层只画最后一根。
    /// </summary>
    public class CandleFeature : ChartFeature
    {
        // 声明本 Feature 的生命周期阶段，确保在基础坐标轴就绪后执行
        public override FeaturePhase Phase => FeaturePhase.Series;

        // ==========================================
        // 🔌 第一组：核心物理引脚注入
        // ==========================================

        // Viewport 由 ChartFeature 基类持有,由 InternalCompose 从 ViewportManagerFeature.Ports 注入（L6 / §B.2.6）

        /// <summary>Y 轴极值引脚：由外部 AutoScaleFeature 计算后传入</summary>
        public DataPort<RealRange> YRangePort { get; init; } = null!;

        /// <summary>💥 极致瘦身：蜡烛图专属数据引脚全家桶（包含开、高、低、收、时间 5 大维度）</summary>
        public CandlePorts Ports { get; init; } = null!;

        // ==========================================
        // 🎨 第二组：动态元数据与样式配置 (零硬编码)
        // ==========================================

        /// <summary>图例分组名称</summary>
        public string GroupName { get; set; } = "Candle";

        /// <summary>开盘价元数据（支持外部不传，自带极简灰白配色兜底）</summary>
        public FieldMeta OpenMeta { get; set; } = FieldMeta.Literal("开", new HevoSolidBrush(Colors.Gray), "F2");
        /// <summary>最高价元数据</summary>
        public FieldMeta HighMeta { get; set; } = FieldMeta.Literal("高", new HevoSolidBrush(Colors.Gray), "F2");
        /// <summary>最低价元数据</summary>
        public FieldMeta LowMeta { get; set; } = FieldMeta.Literal("低", new HevoSolidBrush(Colors.Gray), "F2");
        /// <summary>收盘价元数据</summary>
        public FieldMeta CloseMeta { get; set; } = FieldMeta.Literal("收", new HevoSolidBrush(Colors.Gray), "F2");

        /// <summary>蜡烛图的物理绘制样式（包含红绿柱颜色、线宽等）</summary>
        public CandleStyle Style { get; init; } = CandleStyle.Default;

        /// <summary>底层图层标识符，用于调试和层级管理</summary>
        public string LayerName { get; init; } = "CandleLayer";

        // ==========================================
        // 🛡️ 第三组：冷热分离图层引擎
        // ==========================================

        // 静态层：画历史段 [0, len-1)。tick 只改最后一根时不重录;视口 / 量程 / 尺寸变了(含拖拽、缩放)整层重录。
        private readonly CandleLayer _staticLayer = new();
        // 动态层：永远只负责绘制“最后一根”正在实时跳动的 K 线，以及挂载十字光标等热数据。
        private readonly CandleLayer _dynamicLayer = new() { Level = ChartLayerType.Interaction };

        // 静态层上次录制时四列的引用版本(DataBlackboard.GetIdentityVersion)。只存令牌、不存列引用:
        // 列缓冲会被纪元回收重写(开发规范《DataBlackboard 与 UsePort 物理读取铁律》第 5 条),存引用跨帧比较会被
        // 缓冲池 A → B → A 换回同一块数组骗过;引用版本在每次换块时都变,换回来也算变。
        private VersionToken _staticOpen, _staticHigh, _staticLow, _staticClose;

        /// <summary>
        /// 💥 初始化图层与注册元数据 (仅在组件挂载时执行一次)
        /// </summary>
        protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            _staticLayer.Name = $"{LayerName}_Static";
            _dynamicLayer.Name = $"{LayerName}_Dynamic";

            AttachLayer(_staticLayer);
            AttachLayer(_dynamicLayer);

            // 💥 彻底干掉硬编码！将外部配置好的 Meta 打包发布给底层系统，供 Tooltip 等交互组件消费
            ctx.For(_dynamicLayer).PublishData(new MetaTrait(GroupName,
                OpenMeta, HighMeta, LowMeta, CloseMeta
            ));

            // 红涨绿跌:装配时发布一次。列由取色方(Header / Crosshair)按本帧读到的 DoubleSeriesDataTrait 传进来,
            // 不捕获列、不每帧 new 闭包
            ctx.For(_dynamicLayer).PublishData(new DynamicColorTrait(CandleColor));

        }

        /// <summary>
        /// 💥 核心渲染泵：每一帧数据的极速计算与分发
        /// </summary>
        protected override void OnProject(FeatureContext ctx)
        {
            // 1. 象限查脏：从全家桶中极速提取数据并判断是否发生变动
            var (xRange, xChanged) = ctx.UsePort(Viewport.ActiveRange);
            var (yRange, yChanged) = ctx.UsePort(YRangePort);
            var (opens, opensChanged) = ctx.UsePort(Ports.Open);
            var (highs, highsChanged) = ctx.UsePort(Ports.High);
            var (lows, lowsChanged) = ctx.UsePort(Ports.Low);
            var (closes, closesChanged) = ctx.UsePort(Ports.Close);

            // 2. 物理尺寸查脏：缓存图表区域大小变化
            HevoRect currentArea = ctx.PlotArea;
            var (_, areaChanged) = ctx.UseMemo(currentArea, area => area);

            int len = opens.Length;
            if (len == 0 || !xRange.IsValid || !yRange.IsValid) return;

            // 3. 剥离热数据：无论屏幕上有多少根 K 线，动态层永远只认最后一根！
            int dynamicStartIndex = Math.Max(0, len - 1);

            // 静态层只画历史段 [0, len-1)。实时 tick 原地改最后一根后 ForceWrite,OHLC 列版本每 tick 都变,
            // 但列的引用版本不变 —— 列版本变了还要再看引用版本,四列都没变才算"只动了最后一根",静态层保持上次录制。
            // 发布约定:历史段交出去后不原地改,只有最后一根可以原地改;追加、扩容、写时复制都会换引用。
            // 判脏只比版本令牌,不比内容、不存列引用。
            // （切片器删除后无 Offset 概念：数据是全量、世界索引 == 数组下标）
            var idOpen = ctx.PortIdentity(Ports.Open);
            var idHigh = ctx.PortIdentity(Ports.High);
            var idLow = ctx.PortIdentity(Ports.Low);
            var idClose = ctx.PortIdentity(Ports.Close);
            bool columnsChanged = (opensChanged || highsChanged || lowsChanged || closesChanged)
                && !(idOpen == _staticOpen && idHigh == _staticHigh && idLow == _staticLow && idClose == _staticClose);
            bool needRedrawStatic = areaChanged || xChanged || yChanged || columnsChanged;

            if (needRedrawStatic)
            {
                _staticOpen = idOpen; _staticHigh = idHigh; _staticLow = idLow; _staticClose = idClose;
                ctx.For(_staticLayer).PublishData(new XAxisTrait(xRange));
                ctx.For(_staticLayer).UpdateYAxis(yRange);
                ctx.For(_staticLayer).PublishData(Style);

                // 静态层渲染 [0, len-1) 的历史 K 线，StartIndex 即世界索引 0
                ctx.For(_staticLayer).PublishData(new CandleData(
                    StartIndex: 0,
                    opens.Slice(0, dynamicStartIndex),
                    highs.Slice(0, dynamicStartIndex),
                    lows.Slice(0, dynamicStartIndex),
                    closes.Slice(0, dynamicStartIndex)
                ));
            }

            // ------------------------------------------
            // 🔥 动态层处理区：以下代码专为实时跳动的心跳行情服务
            // ------------------------------------------

            ctx.For(_dynamicLayer).PublishData(new XAxisTrait(xRange));
            ctx.For(_dynamicLayer).UpdateYAxis(yRange);
            ctx.For(_dynamicLayer).PublishData(Style);

            // 截取最后一根 K 线的数据切片
            int dynamicLen = len - dynamicStartIndex;
            var openSlice = opens.Slice(dynamicStartIndex, dynamicLen);
            var highSlice = highs.Slice(dynamicStartIndex, dynamicLen);
            var lowSlice = lows.Slice(dynamicStartIndex, dynamicLen);
            var closeSlice = closes.Slice(dynamicStartIndex, dynamicLen);

            // 动态层只渲染最后一根，StartIndex = 该根的世界索引
            ctx.For(_dynamicLayer).PublishData(new CandleData(
                StartIndex: dynamicStartIndex,
                openSlice, highSlice, lowSlice, closeSlice
            ));

            // ==========================================
            // 💥 交互支持：向引擎发布全量 0-GC 浮点数组！
            // [注意]：必须传当前视口的全量切片，因为 Tooltip 和 Crosshair 是全局十字光标检索，
            // 它们需要完整的数据集来匹配鼠标位置。
            // ==========================================
            ctx.For(_dynamicLayer).PublishData(new DoubleSeriesDataTrait(
                opens, highs, lows, closes
            ));

        }

        // 红涨绿跌取色(DynamicColorTrait):第一根跟自己的开盘价比,后面的跟上一根收盘价比。
        // 列取自调用方本帧读到的 DoubleSeriesDataTrait(本 Feature 发布的顺序是 开 / 高 / 低 / 收)。
        private IHevoBrush CandleColor(DoubleSeriesDataTrait data, int fieldIdx, int localIdx)
        {
            var opens = data.FieldValues[0].Span;
            var closes = data.FieldValues[3].Span;
            if ((uint)localIdx >= (uint)closes.Length) return Style.UpBrush;
            if (localIdx == 0) return closes[0] >= opens[0] ? Style.UpBrush : Style.DownBrush;
            return closes[localIdx] >= closes[localIdx - 1] ? Style.UpBrush : Style.DownBrush;
        }
    }
}
