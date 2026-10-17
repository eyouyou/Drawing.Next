using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Features
{
    // ==========================================
    //  计算 Feature 基类(board → board,不挂图层)
    //
    //  统一抽象:画布上任何"转换节点"都是 input port → 黑盒 → output port,
    //            底层用什么 (Ingestor / IComputeNode / flow.Watch / ctx.UsePort) 是实现细节。
    //
    //  对应传统 schema 里 _dataSource.Pipe() 那一串 fluent 链:
    //      .Inject(ds => ds.PreClose).ForwardTo(PreClosePort)            ← ScalarMappings + edge 已覆盖
    //      .Inject(ds => ds.LogicalLength).ForwardTo(VP.LogicalLength)   ← 已升级:画布上拖到 Viewport 节点
    //      .LinkStream(cfg => cfg.AutoMap())                             ← VectorMappings 已覆盖
    //      .Compute<S>(s, action)                                        ← Feature 层对应 ComputeFeature(默认)
    //
    //  ⚠️ Source Ingestor (ScalarMappings / VectorMappings) 因为依赖 DataSnapshot<TItem>,
    //     必须装在 UniversalDataPipe 的 ingestor 链上才能拿到 snapshot,框架不允许 ChartFeature
    //     直接读 snapshot —— 所以这一类继续走 DataSource.ScalarMappings/VectorMappings 字段,
    //     由 DynamicChartSchema 反射装配。
    //
    //  ✅ Board → Board 的 Compute,直接派 ChartFeature 即可,跟 Series Feature 同体系:
    //     输出端口在 OnComputeCompose 里 flow.WatchAsync(...) 订阅输入、后台算完再提交(Compute-then-Commit)。
    //  💥 铁律:OnProject 帧路径只读,严禁 board.Write* —— 帧内只能 UsePort 取值 + PublishData 发 Trait。
    // ==========================================

    /// <summary>
    /// 计算 Feature 的基类:<see cref="ComputeFeature"/> / <see cref="IncrementalComputeFeature"/> 都派生自它,
    /// GraphViewer 按它把计算节点归入 Series 泳道。
    /// <para>
    /// <b>业务优先用 <see cref="ComputeFeature"/></b>(线程、锁、缓冲池、异常、输出路由都已处理)。
    /// 只有现成的不够用时才自己继承本类(进阶):非 double 列 / 标量输出、强类型参数;
    /// 跨调用状态先看 <see cref="IncrementalComputeFeature"/>。
    /// </para>
    /// <para>
    /// 子类的玩法:
    /// <list type="number">
    ///   <item>声明 DataPort&lt;T&gt; 输入属性(默认 Input 方向),DataPort&lt;T&gt; 输出属性贴 <c>[PortDirection(Output)]</c>。</item>
    ///   <item>在 <see cref="OnComputeCompose"/> 里 <c>flow.WatchAsync(...)</c> 订阅输入、后台算完写输出端口(Compute-then-Commit)。
    ///         写端口只能在这里做:<see cref="OnComputeProject"/> 走帧路径,按铁律只读黑板、只发 Trait。</item>
    ///   <item>无视觉层。GraphViewer 把它当成 Series 类看待(端口可视、可连线、可双击编辑参数)。</item>
    /// </list>
    /// 同 <see cref="UniversalAutoScaleFeature"/>(也算 Compute,只不过把"输出量程"耦合进了视觉链)
    /// 的差异:Compute 节点纯粹 board → board,不挂 layer。
    /// </para>
    /// <para>
    /// 跟 Source Ingestor (ScalarMappings/VectorMappings) 的差异:
    /// 后者直接吃 <see cref="DataSnapshot{TItem}"/>,蓝图侧通过 DataSource.ScalarMappings 反射装 ContextIngestor;
    /// 这个基类吃的是 DataPort<T>,蓝图侧通过 PortBindings 走标准的 Feature 端口焊接路径。
    /// </para>
    /// </summary>
    public abstract class ComputeFeatureBase : ChartFeature
    {
        /// <summary>
        /// 默认 50:夹在 Layout(0)之后、Scale(100)之前 —— 计算结果要先于自动量程与序列投影就绪。子类可重载。
        /// </summary>
        public override FeaturePhase Phase => (FeaturePhase)50;

        // 子类自己声明:
        //   public DataPort<T> InputPort { get; init; } = null!;
        //   [PortDirection(PortDirection.Output)]
        //   public DataPort<T> OutputPort { get; init; } = null!;

        protected sealed override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow)
        {
            OnComputeCompose(chart, ctx, flow);
        }

        /// <summary>子类可选重载:订阅 board 写入链(<c>flow.Watch(...)</c>) 等。</summary>
        protected virtual void OnComputeCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }

        protected sealed override void OnProject(FeatureContext ctx)
        {
            OnComputeProject(ctx);
        }

        /// <summary>
        /// 帧路径钩子(只读):需要时 <c>ctx.UsePort</c> 取值、<c>PublishData</c> 发 Trait;不写黑板。
        /// 纯计算节点通常留空,输出在 <see cref="OnComputeCompose"/> 里提交。
        /// </summary>
        protected abstract void OnComputeProject(FeatureContext ctx);
    }
}
