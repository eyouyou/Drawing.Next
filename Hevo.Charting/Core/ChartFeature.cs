using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Core
{
    public enum FeaturePhase
    {
        // 修复 H4：将原本的魔数 (FeaturePhase)(-5) 替换为具名枚举值。
        // PreLayout 保证交互引擎在布局计算前率先确定视口与碰撞状态。
        // 预留 [-10, 0) 区间供未来插入其他前置阶段，不影响现有排序。
        PreLayout = -10,  // 交互前置（视口/碰撞预算）
        Layout = 0,       // 算地盘
        Scale = 100,      // 算刻度
        Series = 200,     // 画图表（默认）
        Interaction = 300 // 画交互
    }

    // ==========================================
    // 💥 引擎大管家:FeatureContext
    // 热路径(每帧重绘)绝对零分配,全部复用已有对象!
    // ==========================================
    // Use<T> 状态的 key。按引用比作用域:图层可能重写 Equals,这里要的是"同一个对象"。
    internal readonly struct TraitUseKey : IEquatable<TraitUseKey>
    {
        public readonly object Scope;
        public readonly Type TraitType;

        public TraitUseKey(object scope, Type traitType)
        {
            Scope = scope;
            TraitType = traitType;
        }

        public bool Equals(TraitUseKey other) => ReferenceEquals(Scope, other.Scope) && TraitType == other.TraitType;
        public override bool Equals(object? obj) => obj is TraitUseKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Scope), TraitType);
    }

    public partial class FeatureContext
    {
        internal RenderContext _renderCtx = null!;
        internal DataBlackboard _board = null!;
        internal SubscriptionRegistry _registry = null!;
        internal Feature _currentFeature = null!;

        // 这里的 _isFullPass 实际上是由外部 ReactiveSchema 传入的 isEnvironmentSync
        // 代表环境纪元是否发生了改变(如 SizeChanged)
        internal bool _isFullPass;

        // 💥 终极修复:彻底废弃易溢出的 _portTicks,换成 0-GC 的无限版本令牌!
        internal readonly Dictionary<object, VersionToken> _portTokens = new();

        // _board / _renderCtx 只在 BeginProject 到 EndProject 之间是当前帧的(且在 ProjectAll 的读锁内);
        // 帧外(OnCompose / Watch 回调 / 事件)调 Hook 会读到上一帧的引用甚至 null,Debug 下直接报出调用点。
        internal bool _isProjecting;

        // 每次 BeginProject 自增;UseMemo 用它在 Debug 下查"同一调用位置一帧内命中两次"。0 留给"从没命中过"。
        internal int _projectFrame;

        internal void BeginProject(RenderContext render, DataBlackboard board)
        {
            _renderCtx = render;
            _board = board;
            _isProjecting = true;
            if (++_projectFrame == 0) _projectFrame = 1;
        }

        internal void EndProject() => _isProjecting = false;

        [System.Diagnostics.Conditional("DEBUG")]
        internal void AssertProjecting(string hook)
        {
            if (!_isProjecting)
                throw new InvalidOperationException(
                    $"{_currentFeature?.GetType().Name ?? "<feature>"} 在 OnProject 帧路径以外调了 {hook}:" +
                    "OnCompose / Watch 回调 / 事件里没有当前帧的黑板与读锁,改在 OnProject 里读。");
        }

        // 修复 H1 配套:Decompose 时清空跨帧状态。
        // 同一 Feature 实例可能被 Transact 重新装配(hot-plug),残留的 _portTokens / _hookPocket 会
        // 让重装后的首帧脏检查认为"已对齐"而跳过重绘,因此卸载时必须显式归零。
        internal void Reset()
        {
            _portTokens.Clear();
            _hookPocket.Clear();
            _traitSeen.Clear();
            _isProjecting = false;
            _renderCtx = null!;
            _board = null!;
            _registry = null!;
            _currentFeature = null!;
        }

        // ==========================================
        // 💥 图层与共享状态代理
        // ==========================================
        // 返回的代理带上本 ctx,Use<T>() 才知道"上次读到的"记在哪个 Feature 上;Read / PublishData 不受影响。
        public VisualProxy<IVisualData> Shared() => _renderCtx.Shared().WithFeature(this);
        public VisualProxy<IChartLayer> For(IChartLayer layer) => _renderCtx.For(layer).WithFeature(this);

        /// <summary>
        /// 泛型重载:保留具体 layer 类型,让强类型 <c>PublishData&lt;TLayer, TData&gt;</c> 在跨程序集
        /// (如 Charting.PythonNet 内 PyPlotFeature)调用时仍能命中正确的 IConsumes&lt;TData&gt; 约束。
        /// 业务侧 typed call: <c>ctx.For&lt;LineLayer&gt;(_lineLayer).PublishData(new XAxisTrait(...))</c>。
        /// </summary>
        public VisualProxy<TLayer> For<TLayer>(TLayer layer) where TLayer : IChartLayer
            => _renderCtx.For(layer).WithFeature(this);

        // Use<T> 的"上次读到的 Trait":key = (作用域, Trait 类型),作用域是图层对象或共享区的 live 袋子。
        // 跟 _portTokens 一样按对象而不是调用顺序存,Reset 时清空,重装后首帧重新报变化。
        internal readonly Dictionary<TraitUseKey, object?> _traitSeen = new();

        internal (T? Value, bool IsChanged) UseTrait<T>(object scope, T? value) where T : class
        {
            AssertProjecting("Use<T>");
            var key = new TraitUseKey(scope, typeof(T));
            if (_traitSeen.TryGetValue(key, out var last) && ReferenceEquals(last, value))
                return (value, false);
            _traitSeen[key] = value;
            return (value, true);
        }

        /// <summary>
        /// 💥 隐式查脏核心:值类型防抖,不生成任何对象,仅返回 struct Tuple
        /// <para>
        /// 版本令牌按端口对象存,不依赖调用顺序,可以放进分支:某帧没读的端口不更新令牌,下次读到时 IsChanged 跟本 Feature 上次读到它时比。
        /// 端口第一次被读到才订阅;同一帧对同一端口读两次,只有第一次能看到 IsChanged = true。
        /// </para>
        /// </summary>
        // 只追踪 DataPort 的 VersionToken。Trait 变没变用 ctx.For(layer).Use<T>() / ctx.Shared().Use<T>();
        // flow.Watch / WatchAsync 的端口订阅是另一套机制。Trait 变化不会唤醒 Feature,见 01 号文档「Use 体系」。
        public (T Value, bool IsChanged) UsePort<T>(
            DataPort<T> port,
            [System.Runtime.CompilerServices.CallerArgumentExpression("port")] string? portExpression = null)
        {
            // 端口非空契约:UsePort 的语义("订阅 + 读 + 防抖")在 port=null 下没有合法路径。
            // 提前 fail-fast 替代 board.Read 内深处的 NRE,堆栈直接定位到出错的 feature + 字段。
            // CallerArgumentExpression 自动捕获 caller 写的字面量(`ctx.UsePort(DataPort)` → "DataPort"),
            // 业务侧 0 改动;不预设 caller 是哪种 port 来源(init / 局部变量 / 动态生成)。
            if (port is null)
                throw new ArgumentNullException(
                    portExpression,
                    $"{_currentFeature?.GetType().Name ?? "<feature>"}.{portExpression} is null — " +
                    "ctx.UsePort 不接受 null 端口,调用前确保端口已装配。");
            AssertProjecting(nameof(UsePort));

            // 1. 0-GC 拉取物理数据
            T val = _board.Read(port);

            // 2. 拉取该引脚在黑板上的最新纪元令牌
            VersionToken currentToken = _board.GetVersion(port);
            bool changed = false;

            // 3. 💥 架构级防御:用 VersionToken 对齐版本 + 顺手做"首次访问"探测
            //    隐式 Subscribe 只在 Feature 首次见到 port 时跑一次,跨帧稳态零 lock / 零 dict 改动。
            //    Decompose 走 _context.Reset() 会清空 _portTokens,Transact 重装后首帧自动重新订阅。
            if (!_portTokens.TryGetValue(port, out var lastToken))
            {
                // 首次:登记订阅 + 落桩 token
                if (_currentFeature != null && _registry != null) _registry.Subscribe(port, _currentFeature);
                _portTokens[port] = currentToken;
                changed = true;
            }
            else if (lastToken != currentToken)
            {
                _portTokens[port] = currentToken;
                changed = true;
            }

            // 4. 环境突变拦截:如果外部(如 SizeChanged)要求全量重绘
            // 则强制将本帧获取的所有数据视为"变脏",以触发下游彻底重新计算投影
            if (_isFullPass) changed = true;

            return (val, changed);
        }

        /// <summary>
        /// 列端口的引用版本(见 <see cref="DataBlackboard.GetIdentityVersion"/>):底层引用换了才变,原地改再 ForceWrite 不变。
        /// 只读令牌,不订阅、不占 Hook 槽位;配合同一端口的 <see cref="UsePort{T}"/> 使用(先 UsePort 订阅,再看引用变没变)。
        /// </summary>
        public VersionToken PortIdentity<T>(DataPort<ReadOnlyMemory<T>> port)
        {
            AssertProjecting(nameof(PortIdentity));
            return _board.GetIdentityVersion(port);
        }

        /// <summary>
        /// 动态批量引脚订阅:端口个数运行期才知道时,一次读一组,任一端口变了就返回 true。
        /// 允许安全地订阅一组引脚,数据直接写入 buffer,全程 0-GC。
        /// </summary>
        public bool UsePorts<T>(
            DataPort<T>[] ports,
            T[] buffer,
            [System.Runtime.CompilerServices.CallerArgumentExpression("ports")] string? portsExpression = null)
        {
            if (ports.Length != buffer.Length)
                throw new ArgumentException("Ports 数组和 Buffer 数组的长度必须一致!");
            AssertProjecting(nameof(UsePorts));

            bool changed = false;

            for (int i = 0; i < ports.Length; i++)
            {
                var port = ports[i];

                // 数组扇入:逐元素 null check,定位到具体索引,中性 message 不预设来源。
                if (port is null)
                    throw new ArgumentNullException(
                        $"{portsExpression}[{i}]",
                        $"{_currentFeature?.GetType().Name ?? "<feature>"}.{portsExpression}[{i}] is null — " +
                        "ctx.UsePorts 不接受数组中含 null 的端口,调用前确保所有端口已装配。");

                // 0-GC 物理拉取
                buffer[i] = _board.Read(port);

                // 顺手用 _portTokens 是否存在条目作为"首次访问"信号 —— 首次才做隐式订阅。
                VersionToken currentToken = _board.GetVersion(port);
                if (!_portTokens.TryGetValue(port, out var lastToken))
                {
                    if (_currentFeature != null && _registry != null)
                        _registry.Subscribe(port, _currentFeature);
                    _portTokens[port] = currentToken;
                    changed = true;
                }
                else if (lastToken != currentToken)
                {
                    _portTokens[port] = currentToken;
                    changed = true;
                }
            }

            // 环境突变拦截
            if (_isFullPass) changed = true;

            return changed;
        }

        public HevoRect PlotArea
        {
            get
            {
                return _renderCtx.GetPlotArea();
            }
        }
    }


    // ==========================================
    // 💥 Chart 业务特化 feature 基类。
    // 通用 reactive 基础设施(layer / lifecycle / IDisposable / IPausable / Phase / IsSingleton /
    // FeatureContext / OnCompose+flow / OnProject / Project 帧入口 / 嵌套子 feature 等)全部由
    // <see cref="Feature"/> 基类承担(Phase 2/Route A);本类只补一项 chart 专属概念:Viewport
    // (1D 量程,LogicalLength + UserRange + ActiveRange,概念上不适用于 graph editor 等 2D 场景)。
    // ==========================================
    public abstract class ChartFeature : Feature
    {
        // 顶层视口(L6 / §B.2.6):ComposeCore 阶段从 ChartCell attached property 一次性注入字段,
        // 后续业务 Feature 直接读 this.Viewport.X 是字段访问,不走 WPF DP。
        //
        // 时序保证:
        //   InitializeRegistry: EnsureBaseFeatures(挂 PortsFeature) → DefineFeatures(业务挂各 ChartFeature)
        //                       → Decorate(LinkedMaster/Pane 阶段 mutate PortsFeature.Ports = SharedViewport,
        //                          setter 内部 SetAttached 同步更新 ChartCell 上的 attached property)
        //   BuildAndActivatePipeline: 按 Phase 序遍历 _orderedFeatures,调 InternalCompose → ComposeCore →
        //                             ★ 此时 attached ports 已是最终值,字段一次注入即可
        //   ⇒ Decorate 之后再没人 mutate Ports;字段方案不会拿到过时引用。
        //
        // 错误时机访问保护:在 ComposeCore 之前(罕见:子类在 ctor/OnAttached 内提前读)抛 InvalidOperation,
        // 而非 NRE,定位清晰。

        private ViewportPorts? _viewport;

        /// <summary>
        /// 当前 schema 的 viewport ports。InternalCompose 之后稳定可用;之前 access 抛 InvalidOperation。
        /// </summary>
        public ViewportPorts Viewport => _viewport
            ?? throw new InvalidOperationException(
                $"{GetType().Name}.Viewport 在 ComposeCore 之前被访问。" +
                "ChartFeature 应仅在 OnCompose / OnProject / Watch 回调中读 viewport,不要在 ctor / OnAttached 中读。");

        private Interaction.PointerPorts? _pointer;

        /// <summary>
        /// 当前 schema 的指针路由器端口(<see cref="Interaction.PointerPorts.GesturePort"/>)。同 <see cref="Viewport"/>:
        /// InternalCompose 之后可用;不是图表 schema(没挂路由器)时访问抛 InvalidOperation。
        /// </summary>
        public Interaction.PointerPorts Pointer => _pointer
            ?? throw new InvalidOperationException(
                $"{GetType().Name}.Pointer 不可用:ComposeCore 之前访问,或者所在 schema 不是 ChartReactiveSchema(没有指针路由器)。");

        protected override void ComposeCore(ChartCell chart, RenderContext ctx)
        {
            base.ComposeCore(chart, ctx);
            // Decorate 已跑过,attached ports 此时是最终值(LinkedMaster/Pane 的 SharedViewport 或独立 schema 的私 ports)。
            _viewport = ViewportPorts.RequireAttached(chart);
            _pointer = chart.PointerRegions.Ports;
        }

        protected override void OnDecompose()
        {
            base.OnDecompose();
            // Transact 热插拔场景:同一 feature 实例 Decompose 后可能再次 Add → 再次 ComposeCore 重新注入。
            // 这里 reset null 让"未注入就读"的协议错误能被 throwing getter 捕获。
            _viewport = null;
            _pointer = null;
        }
    }

    // ==========================================
    // 💥 Rx 流的生命周期绑定扩展
    // ==========================================
    public static class RenderFlowLifecycleExtensions
    {
        /// <summary>
        /// 💥 将当前 Rx 流的生命周期强制托管给指定的 Feature。
        /// Feature 被卸载时自动 Dispose 该流,且 Suspend / Resume 期间正确级联——绝不泄漏内存。
        /// </summary>
        public static IRenderFlow<T> OwnedBy<T>(this IRenderFlow<T> source, Feature feature)
        {
            var intercepted = new WorkflowEngine<T>((next, error) =>
            {
                // 执行实际的订阅并拿到句柄
                var token = source.Subscribe(next, error);

                // 将句柄托管给 Feature 的黑洞口袋
                feature.RegisterDisposable(token);

                return token;
            });
            // 重新穿上马甲,保持图表上下文
            return intercepted.BindTo(source.Chart);
        }
    }
}
