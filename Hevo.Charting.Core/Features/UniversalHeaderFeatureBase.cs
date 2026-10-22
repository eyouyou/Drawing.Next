using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Diagnostics;

namespace Hevo.Charting.Features
{
    public static class UniversalHeaderFeatureExtensions
    {
        /// <summary>
        /// 装配万能联动头:
        /// 业务侧零 viewport 字眼,helper 内部由头部 Feature 的 OnCompose 自己从
        /// base ChartFeature.Viewport(<see cref="ViewportPorts.GetAttached"/>)取 LogicalLength,
        /// 调用顺序跟 SetupViewport / 其他 helper 完全自由(不依赖装配时机)。
        /// 头部是宿主的外壳 UI(WPF:UniversalHeaderFeature;Avalonia:AvaloniaUniversalHeaderFeature),由宿主注册;
        /// 同一个 schema 挂到哪个宿主就出哪个宿主的头部。
        /// </summary>
        /// <param name="labelBrush">字段名(含冒号)的画刷;null = 宿主默认灰。传资源画刷跟主题走。</param>
        /// <param name="valueBrush">字段值的画刷;null = 宿主默认白。</param>
        public static EnvironmentBuilder SetupUniversalHeader(this EnvironmentBuilder builder, DataPort<PointerHitState?> hitPort,
            IHevoBrush? labelBrush = null, IHevoBrush? valueBrush = null)
        {
            var host = ChartHostServices.ResolveForCompose(builder.Canvas);
            var create = host.CreateUniversalHeader
                ?? throw new InvalidOperationException($"宿主 {host.Name} 没有注册联动头(ChartHostServices.CreateUniversalHeader)。");
            var header = create(hitPort);
            header.LabelBrush = labelBrush;
            header.ValueBrush = valueBrush;
            builder.Canvas.Add(header);
            return builder;
        }
    }

    /// <summary>头部的一个字段:来自哪个图层的第几列,以及它的显示元数据。</summary>
    public sealed class UniversalHeaderField
    {
        public UniversalHeaderField(FieldMeta meta, IChartLayer sourceLayer, int fieldIndex)
        {
            Meta = meta; SourceLayer = sourceLayer; FieldIndex = fieldIndex;
        }

        public FieldMeta Meta { get; }
        public IChartLayer SourceLayer { get; }
        public int FieldIndex { get; }
    }

    /// <summary>
    /// 万能联动头的平台无关部分:选哪一根(命中的那根,没命中取末根)、收集哪些字段(所有带 MetaTrait 的图层)、
    /// 值怎么格式化(NaN 显示 "--")、动态颜色。宿主子类只管控件:
    /// 字段组成变了时 <see cref="RebuildFields"/> 重建一次控件,之后每帧 <see cref="UpdateField"/> 只改文本 / 圆点颜色。
    /// <para>
    /// <b>数据流</b>:命中索引 → 遍历所有活跃 Layer 的 MetaTrait/DoubleSeriesDataTrait → 只更新文本(不重建 UI)。
    /// 指标数量 / 名字 / 颜色变化时,通过 fingerprint 比对触发重建;之后所有更新都走宿主的对象池,0 控件分配。
    /// </para>
    /// </summary>
    public abstract class UniversalHeaderFeatureBase : ChartFeature, IHostDecorator
    {
        public override FeaturePhase Phase => FeaturePhase.Interaction;

        /// <summary>命中状态端口:有命中则显示对应索引的字段值;无命中则回落到末根(实时数据)。</summary>
        public DataPort<PointerHitState?> HitStatePort { get; init; } = null!;

        /// <summary>
        /// 逻辑数据长度端口:用于在数据切换时同步触发头部刷新(主要起 Watch 触发器作用)。
        /// 业务/蓝图初始化时显式喂;留空(默认 null)时 OnCompose 自动 fallback 到
        /// <c>base.Viewport.LogicalLength</c>。
        /// init-only 满足 HEVO007 引脚不可变约束;运行期 fallback 走下面的 _effectiveLengthPort 内部字段。
        /// </summary>
        public DataPort<int>? LogicalLengthPort { get; init; }

        /// <summary>字段名(含冒号)的画刷;null = 宿主默认灰。装配前设,宿主重建控件时用。</summary>
        public IHevoBrush? LabelBrush { get; set; }

        /// <summary>字段值的画刷;null = 宿主默认白。装配前设,宿主重建控件时用。</summary>
        public IHevoBrush? ValueBrush { get; set; }

        // OnCompose 期决定实际订阅的 length port —— 业务显式喂的优先,否则 fallback Viewport.LogicalLength。
        // 不能直接写 LogicalLengthPort(init 限制),也不该用 set 让蓝图反射误覆写(HEVO007)。
        // 协议占位符 `null!`:框架时序保证 OnCompose 必先于 OnProject 跑过,使用窗口内非 null。
        private DataPort<int> _effectiveLengthPort = null!;

        // 当前字段表(重建时整体换新,跟宿主控件一一对应)
        private readonly List<UniversalHeaderField> _fields = new();
        // 上一次所有 MetaTrait 的复合哈希。指标增删/重命名/换色时 fingerprint 跳变 → 重建。
        private int _lastMetaFingerprint = -1;

        /// <summary>字段组成变了:按 <paramref name="fields"/> 的顺序重建控件,值先显示 "--"。</summary>
        protected abstract void RebuildFields(IReadOnlyList<UniversalHeaderField> fields);

        /// <summary>
        /// 刷新第 <paramref name="index"/> 个字段的显示值;<paramref name="dotBrush"/> 非 null 时圆点换成这个颜色(动态着色的图层)。
        /// </summary>
        protected abstract void UpdateField(int index, string text, IHevoBrush? dotBrush);

        protected override void OnCompose(IChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            // 业务/蓝图没显式传 LogicalLengthPort 时,从 base ChartFeature.Viewport 取。
            // OnCompose 阶段所有 features 已 add 完,PortsFeature.OnAttached 必跑过,Viewport 字段已就位。
            _effectiveLengthPort = LogicalLengthPort ?? Viewport.LogicalLength;
        }

        protected override void OnProject(FeatureContext ctx)
        {
            var (hitState, hitChanged) = ctx.UsePort(HitStatePort);
            var (totalLen, lenChanged) = ctx.UsePort(_effectiveLengthPort);

            // 1. 当前图层元数据指纹
            int currentFingerprint = ComputeFingerprint(ctx);

            // 2. 指纹变了:指标被添加/删除,或者语言/主题变了,重建 UI
            if (currentFingerprint != _lastMetaFingerprint)
            {
                CollectFields(ctx);
                RebuildFields(_fields);
                _lastMetaFingerprint = currentFingerprint;
            }

            // 3. 热路径刷新:只改文本,不新建控件
            for (int i = 0; i < _fields.Count; i++)
            {
                var field = _fields[i];
                var proxy = ctx.For(field.SourceLayer);
                var doubleData = proxy.Read<DoubleSeriesDataTrait>();
                if (doubleData == null || field.FieldIndex >= doubleData.FieldValues.Length) continue;

                var span = doubleData.FieldValues[field.FieldIndex].Span;
                if (span.Length == 0) continue;

                // 鼠标移出时,默认拿屏幕最右侧的最新数据
                int targetIndex = hitState.HasValue && !hitState.Value.IsOutOfBounds
                    ? hitState.Value.LocalIndex
                    : span.Length - 1;

                // 诊断:默认态尾部出现 NaN 时,定位是哪根线、尾部连续缺多少格、最后一根有效值在哪。
                // 回退修复后保留这段,以便复现"hover 一次就变 --"时打印根因。
                if (!hitState.HasValue || hitState.Value.IsOutOfBounds)
                {
                    int probe = span.Length - 1;
                    while (probe >= 0 && double.IsNaN(span[probe])) probe--;
                    int tailNaN = (span.Length - 1) - probe;
                    if (tailNaN > 0)
                    {
                        var name = HevoStrings.Resolve(field.Meta.Name);
                        double lastValid = probe >= 0 ? span[probe] : double.NaN;
                        Debug.WriteLine($"[Header] line=\"{name}\" field={field.FieldIndex} spanLen={span.Length} tailNaN={tailNaN} lastValidIdx={probe} lastValidVal={lastValid}");
                    }
                }

                if (targetIndex >= 0 && targetIndex < span.Length)
                {
                    double val = span[targetIndex];
                    string text = double.IsNaN(val) ? "--" : val.FormatValue(field.Meta.Format, field.Meta.Provider);
                    var dynColor = proxy.Read<DynamicColorTrait>();
                    UpdateField(i, text, dynColor?.Evaluator(doubleData, field.FieldIndex, targetIndex));
                }
                else
                {
                    UpdateField(i, "--", null);
                }
            }
        }

        /// <summary>0-GC 指纹计算</summary>
        private int ComputeFingerprint(FeatureContext ctx)
        {
            var hash = new HashCode();
            foreach (var layer in Chart.ActiveLayers)
            {
                var meta = ctx.For(layer).Read<MetaTrait>();
                if (meta == null) continue;

                // FieldMeta 是 record class:编译器自动生成值哈希,HashCode.Add<T> 走泛型不装箱
                for (int i = 0; i < meta.Fields.Length; i++)
                {
                    hash.Add(meta.Fields[i]);
                }
            }
            return hash.ToHashCode();
        }

        // 只在指纹变化时跑(重建路径),热路径不分配
        private void CollectFields(FeatureContext ctx)
        {
            _fields.Clear();
            foreach (var layer in Chart.ActiveLayers)
            {
                var meta = ctx.For(layer).Read<MetaTrait>();
                if (meta == null) continue;
                for (int i = 0; i < meta.Fields.Length; i++)
                    _fields.Add(new UniversalHeaderField(meta.Fields[i], layer, i));
            }
        }
    }
}
