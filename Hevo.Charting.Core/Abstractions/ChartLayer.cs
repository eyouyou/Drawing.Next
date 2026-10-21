using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Abstractions
{
    /// <summary>
    /// 层级接口
    /// </summary>
    public interface IChartLayer : IDisposable
    {
        string Name { get; set; }
        ChartLayerType Level { get; }

        /// <summary>
        /// <summary>
        /// 这是核心产物：一堆录制好的指令
        /// 外部获取产物
        /// </summary>
        RenderBuffer Buffer { get; }

        // 1. 录制阶段 (Update)
        // 职责：清空Buffer -> 计算坐标 -> Push指令
        void Update(IVisualData data);
    }


    /// <summary>
    /// 💥 运行时的无形之手：完美拦截条件分支 (if-else) 下的动态依赖
    /// </summary>
    public readonly struct TrackerDataProxy : IVisualData
    {
        private readonly ChartLayer _layer;
        private readonly IVisualData _snapshot; // 真实的数据快照 (CascadingVisualData)

        public TrackerDataProxy(ChartLayer layer, IVisualData snapshot)
        {
            _layer = layer;
            _snapshot = snapshot;

            // 💥 关键：每次跑 Update 前，清空上一帧的记忆！
            // 保证图层在 if-else 里随时切换分支时，依赖图谱永远是最新的。
            _layer.DependencyTracker.PrepareForNewFrame();
        }

        public T? Get<T>() where T : class
        {
            // 1. 从真实快照中拿数据
            var trait = _snapshot.Get<T>();

            // 3. 💥 指向追踪器：存入录像带
            _layer.DependencyTracker.TrackedRefs[TraitId<T>.Id] = trait;

            return trait;
        }

        public void Publish<T>(T snapshot) where T : class
        {
            // 如果你在 Update 里允许强行写数据，透传即可
            _snapshot.Publish(snapshot);
        }
    }

    /// <summary>
    /// 💥 依赖追踪与保底资产管理器 (彻底为 ChartLayer 瘦身)
    /// </summary>
    internal class VisualDependencyTracker
    {
        // 💥 记忆海马体：记录上一次 Update 时，实际拿到了哪些对象 (Key: TraitId, Value: 对象的内存引用)
        // 字典只在增删键时有微小开销，预热后 0 分配
        // 初始容量 16:典型 layer 跟踪 5-15 个 trait,跳过 0→3→7→17 的 resize 链。
        public Dictionary<int, object?> TrackedRefs { get; } = new(16);

        // 把清理工作也搬过来
        public void PrepareForNewFrame()
        {
            TrackedRefs.Clear();
        }

        public bool CheckIfDirty(VisualDataBag liveLocalBag, VisualDataBag liveGlobalData)
        {
            foreach (var kvp in TrackedRefs)
            {
                int traitId = kvp.Key;
                object? lastSeenObj = kvp.Value;

                // 级联获取: 局部(Bag) > 全局(Bag) > 保底(Dictionary)
                // 全程 O(1) 极速碰撞
                object? currentObj = liveLocalBag.GetById(traitId)
                                  ?? liveGlobalData.GetById(traitId);

                // 只要内存地址变了，立刻判定为脏！
                if (!ReferenceEquals(lastSeenObj, currentObj))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // 只管录制(逻辑);上屏用的平台对象(WPF DrawingVisual 等)由宿主挂在 HostVisual 上,图层本身不依赖任何 UI 框架
    public abstract class ChartLayer : IChartLayer
    {
        public string Name { get; set; } = string.Empty;
        public ChartLayerType Level { get; set; }

        /// <summary>
        /// 绘制策略。Software = WPF 矢量回放(默认);Bitmap = 框架光栅化到位图后再交给 WPF。
        /// 运行时随时可改,下一帧生效(不需要重录图层);也可以用 <see cref="ChartCell.RenderModeOverride"/> 整张图统一切。
        /// </summary>
        public RenderMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                ModeChanged?.Invoke(this);
            }
        }
        private RenderMode _mode = RenderMode.Software;

        /// <summary>ChartCell 挂上的回调:Mode 变了就排一帧,把当前前台指令按新策略重新上屏。</summary>
        internal Action<ChartLayer>? ModeChanged;

        /// <summary>
        /// 宿主为本图层建的上屏对象(WPF 是图层的 DrawingVisual + 位图宿主)。宿主创建和读写;
        /// 图层 Dispose 时若它实现 IDisposable 一并释放。
        /// </summary>
        internal object? HostVisual;

        // ==========================================
        // 双缓冲机制：存的是 LayerBuffer (大礼包)
        // ==========================================
        private LayerBuffer _frontBuffer = new(); // UI 读
        private LayerBuffer _backBuffer = new();  // 后台 写
        private readonly object _lock = new();

        public bool IsDirty { get; protected set; } = false;

        internal void MarkDirty()
        {
            IsDirty = true;
        }

#if DEBUG
        // Debug 诊断:挂上后连续这么多帧都没录出任何指令,就在输出窗口报一次。
        // 120 帧:按需出帧,一次拖拽 / 一秒 tick 推送就有几十帧,数据正常到达的图层早该画出东西;
        // 再短会把"数据还在加载"的图层误报出来。
        internal const int SilentLayerFrameThreshold = 120;

        internal int FramesSinceAttach;
        internal bool HasRecordedContent;
        /// <summary>报过的诊断文本(测试读它;null = 还没报)。</summary>
        internal string? SilentLayerDiagnosis;

        // 每帧 RenderContext.SubmitSync 调一次。只写日志:不抛、不标脏、不改任何渲染状态。
        // 只报一次,只报"从挂上到现在一条指令都没录出来"的图层;被隐藏的帧不计数(隐藏时不画是预期)。
        internal void TickSilentDiagnosis(VisualDataBag liveLocal, VisualDataBag liveGlobal)
        {
            if (HasRecordedContent || SilentLayerDiagnosis != null) return;
            var visibility = liveLocal.Get<VisibilityTrait>() ?? liveGlobal.Get<VisibilityTrait>();
            if (visibility is { IsVisible: false }) return;
            if (++FramesSinceAttach < SilentLayerFrameThreshold) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("[Hevo.Charting] 图层 '").Append(Name).Append("' (").Append(GetType().Name)
              .Append(") 挂上后 ").Append(FramesSinceAttach).Append(" 帧没录出任何指令。");
            if (!IsDiscovered)
            {
                // 深度睡眠:还没被唤醒过,OnUpdate 一次都没跑
                sb.Append("没有喂过数据:OnUpdate 一次都没跑,图层要等第一份发到本层的 Trait 才会被唤醒。")
                  .Append("常见原因:Feature 只往共享作用域发(ctx.Shared()),本层什么都没收到;图层只读共享 Trait;")
                  .Append("用 canvas.AddLayer / AddUnmanagedLayer 挂了层,但没人给它喂数据。")
                  .Append("改法:Feature 里在 OnProject 用 ctx.For(layer).PublishData 发本层的数据 / 样式 Trait;")
                  .Append("不用 Feature 时用 cell.AddUnmanagedLayer(layer) 返回的代理 PublishData。");
            }
            else
            {
                List<string>? nulls = null;
                foreach (var kvp in DependencyTracker.TrackedRefs)
                {
                    if (kvp.Value is null) (nulls ??= new List<string>()).Add(TraitIndexer.NameOf(kvp.Key));
                }
                if (nulls != null)
                    sb.Append("OnUpdate 读到的这些 Trait 在本层和共享作用域里都是 null:").Append(string.Join(", ", nulls))
                      .Append("。检查它们是不是发错了作用域(哪些是共享的见 06 号文档「Trait 作用域」)。");
                else
                    sb.Append("OnUpdate 跑过,读到的 Trait 都在,但没有画任何东西(检查投影区间 / 数据长度)。");
            }
            sb.Append("如果本层就是要等用户操作才画(比如标记层),可忽略。");
            SilentLayerDiagnosis = sb.ToString();
            System.Diagnostics.Debug.WriteLine(SilentLayerDiagnosis);
        }
#endif

        /// <summary>
        /// OnUpdate 是否必须在 UI 线程执行(直接读写 WPF 控件 / DependencyObject 的图层返回 true)。
        /// PlotMode.Parallel 下这类图层不进线程池,留在 UI 线程录制。
        /// </summary>
        protected internal virtual bool RequiresUiThread => false;

        /// <summary>
        /// 所在屏幕的 DIP→物理像素比(100% = 1.0,150% = 1.5)。ChartCell 每帧录制前写入,
        /// 供按物理像素做 LOD 的图层(CandleLayer / LineLayer)使用;不在 ChartCell 里时保持 1.0。
        /// </summary>
        public double PixelsPerDip { get; internal set; } = 1.0;

        internal VisualDependencyTracker DependencyTracker { get; } = new();

        // 💥 是否已经被局部唤醒并完成了第一次采样？
        internal bool IsDiscovered { get; set; } = false;

        // 是否已完成 RequiredTrait 校验(每个 Layer 实例只跑一次)
        private bool _traitsValidated = false;

        /// <summary>
        /// 校验 [RequiredTrait] 标注的硬依赖 trait 是否已被发布。
        /// 默认空实现;Roslyn 生成器(RequiredTraitValidatorGenerator)会为有 [RequiredTrait] 标注的
        /// partial Layer 子类生成强类型 override,运行时 0 反射、0 装箱。
        /// </summary>
        protected virtual void ValidateRequiredTraits(IVisualData data) { }

        // 接口实现：隐式转型为 RenderBuffer 基类
        public RenderBuffer Buffer
        {
            get { lock (_lock) return _frontBuffer; }
        }

        // --- 1. Update (后台录制) ---
        public void Update(IVisualData data)
        {
            // 1. 【通用特质拦截】：引擎级显隐控制
            var visibility = data.Get<VisibilityTrait>();
            if (visibility != null && !visibility.IsVisible)
            {
                // 如果被隐藏了，必须清空后备缓冲，消除残影，并重置脏标记
                lock (_lock) _backBuffer.Clear();
                IsDirty = true; // 标记脏状态，强制 ChartCell 把这层透明画面刷到前台
                IsDiscovered = true;
                return;         // 🚨 0 开销跳过，绝对不执行 OnUpdate
            }

            // 2. 传统图层脏检查
            CheckDirtyState(data);

            // 如果不脏，直接 return！保护 CPU，实现 0 开销跳过
            if (!IsDirty) return;

            LayerBuffer active;
            lock (_lock) active = _backBuffer;

            active.Clear();

            // 3. 首次进入渲染分支时校验 [RequiredTrait] 标注的硬依赖 trait,缺失即 fail-fast。
            if (!_traitsValidated)
            {
                ValidateRequiredTraits(data);
                _traitsValidated = true;
            }

            // 4. 包一层 TrackerDataProxy:在 OnUpdate 内 Get<T> 自动落入 DependencyTracker.TrackedRefs,
            //    供下一帧 RenderContext.SubmitSync 走 Strategy B 做引用相等性判脏。
            //    构造期会清空上一帧记忆,因此仅在 IsDirty=true(确认进入渲染分支)时再创建。
            var tracker = new TrackerDataProxy(this, data);
            OnUpdate(tracker, active.Drawing, active.Widget);
#if DEBUG
            if (active.Drawing.Commands.Count > 0 || active.Bitmap.PixelData != IntPtr.Zero || !active.Widget.IsEmpty)
                HasRecordedContent = true;
#endif

            // 5. 标记已完成首次发现:从下一帧起 SubmitSync 走 Strategy B 自动判脏
            IsDiscovered = true;
        }

        // --- 2. Swap (UI 线程同步) ---
        public void SwapBuffer()
        {
            lock (_lock)
            {
                if (IsDirty)
                {   
                    (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
                }
            }
        }

        // 物理渲染上屏后，由 ChartCell 调用，清理脏标记
        internal void PostRender() => IsDirty = false;

        // 如果子类是 ReactiveChartLayer，它会通过 Bind 流来修改 IsDirty。
        protected virtual void CheckDirtyState(IVisualData data) { }
        // 【子类实现】
        // 这里是你写业务逻辑的地方，画线用 drawSink，放按钮用 widgetSink 
        protected abstract void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink);

        // ==========================================
        // 资源释放 (Dispose Pattern)
        // ==========================================
        private bool _disposedValue;

        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入 "Dispose(bool disposing)" 方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // 1. 清理其他可能的大内存引用，帮助 GC 尽早回收
                    _frontBuffer.Clear();
                    _backBuffer.Clear();
                    (HostVisual as IDisposable)?.Dispose();
                    HostVisual = null;
                    ModeChanged = null;
                }

                // 释放未托管的资源(未托管的对象)并重写终结器
                // 将大型字段设置为 null
                _disposedValue = true;
            }
        }
    }
}
