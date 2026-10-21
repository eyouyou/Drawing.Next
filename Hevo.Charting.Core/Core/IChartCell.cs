using Hevo.Charting.Abstractions;
using Hevo.Charting.DevTools;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.Core
{
    public enum ChartLayerType
    {
        /// <summary>
        /// 图表的背景色、渐变背景、或者可能存在的背景水印
        /// </summary>
        Background = 0,
        /// <summary>
        /// 折线/曲线（MainVisual）、柱状图、K线实体，以及底部的网格线（Grid Lines）
        /// </summary>
        Main = 10,          // 
        /// <summary>
        /// 选中状态、高亮
        /// </summary>
        Selection = 20,     // 
        /// <summary>
        /// 十字准星（Crosshair）、橡皮框（Rubber Band / Zoom Box）、鼠标悬停时的临时标记点。
        /// </summary>
        Interaction = 30,   // 
        /// <summary>
        /// 悬浮提示框（Tooltip/Tip）、标签、图例、或者悬浮在图表上方的操作按钮
        /// </summary>
        Overlay = 40        // 
    }

    /// <summary>
    /// 图表单元的平台无关契约:Feature / Schema / 数据流只认它。WPF 的 <c>ChartCell</c> 实现它,
    /// 别的 UI 框架的宿主实现同一份契约即可复用全部 Feature 与图层。
    /// 需要 WPF 专属能力(路由事件、Dispatcher、ContextMenu)的业务代码把它转回宿主控件类型再用。
    /// </summary>
    public interface IChartCell
    {
        /// <summary>帧管线、图层注册表、黑板等宿主无关的运行时状态。业务代码一般不直接用。</summary>
        ChartEngine Engine { get; }

        /// <summary>当前挂上的 schema(没挂为 null)。换掉时旧 schema 先被卸载。</summary>
        ChartSchema? Template { get; set; }

        /// <summary>宿主控件当前布局尺寸(DIP)。</summary>
        double ActualWidth { get; }
        double ActualHeight { get; }

        /// <summary>true 时宿主显示 loading 蒙层;蓝图路径首帧到达后框架翻回 false。</summary>
        bool IsLoading { get; set; }

        /// <summary>把事务排进下一帧(任何线程都可调)。</summary>
        void RequestUpdate(Action<RenderContext> updateAction);

        /// <summary>当前所有活跃图层(静态层在前、交互层在后,各自按 Z 序)。</summary>
        IReadOnlyList<IChartLayer> ActiveLayers { get; }

        bool HasLayer(string name);
        IChartLayer? GetLayer(string name);

        /// <summary>挂一个不归 Feature 管的图层;调用方负责 <see cref="RemoveUnmanagedLayer"/>。</summary>
        VisualProxy<TLayer> AddUnmanagedLayer<TLayer>(TLayer layer) where TLayer : IChartLayer;
        void RemoveUnmanagedLayer(IChartLayer layer);

        DiagnosticsSnapshot GetDiagnostics();

        /// <summary>彻底卸载 schema、释放 compose 期订阅(cell 不再使用时调)。</summary>
        void Shutdown();
    }

    /// <summary>UI 线程排队的先后:DataBind 早于 Background(Background 在布局与首次绘制之后)。</summary>
    internal enum HostPostPriority { DataBind, Background }

    /// <summary>
    /// 宿主(WPF ChartCell 等)替引擎做的平台相关的事:挂 / 拔每帧回调、图层上屏对象的增删、上屏、指针输入。
    /// 引擎只在 UI 线程调这些方法,<see cref="ArmFrame"/> 例外(见注释)。
    /// </summary>
    internal interface IChartCellHost
    {
        /// <summary>
        /// 第一笔待办入队时调(持引擎调度锁,可能在任意线程):宿主在 UI 线程挂上每帧回调,
        /// 回调里调 <see cref="ChartEngine.OnFrame"/>。静置时不挂,不占 GPU。
        /// </summary>
        void ArmFrame();

        /// <summary>待办换出后调(持调度锁,UI 线程):拔掉每帧回调。</summary>
        void DisarmFrame();

        /// <summary>图层按 Z 序插入第 <paramref name="index"/> 位。overlay = 交互层(跟静态层分开上屏)。</summary>
        void InsertLayerVisual(ChartLayer layer, bool overlay, int index);
        void RemoveLayerVisual(ChartLayer layer, bool overlay);

        /// <summary>图层被移除:销毁它的控件池(不管它在不在矢量注册表里)。</summary>
        void DestroyLayerWidgets(IChartLayer layer);

        /// <summary>图层已从注册表摘除、即将 Dispose(后端释放该图层的缓存)。</summary>
        void OnLayerRemoved(ChartLayer layer);

        /// <summary>
        /// 上屏:图层已 SwapBuffer、尚未清脏标记。脏图层的前台 <see cref="ChartLayer.Buffer"/> 是本帧新指令。
        /// 两组各自按 Z 序;宿主同时负责脏图层的控件(Widget)同步。
        /// </summary>
        void Present(IReadOnlyList<ChartLayer> staticLayers, IReadOnlyList<ChartLayer> overlayLayers);

        /// <summary>Aspect 链变了,重建外壳 UI(还没应用模板时什么也不做)。</summary>
        void RefreshDecoration();

        /// <summary>本宿主给核心的平台实现(悬浮窗图层、外壳 Feature、资源文本、调试菜单)。同一宿主的所有 cell 共用一份。</summary>
        ChartHostServices Services { get; }

        /// <summary>宿主进程正在退出(之后不再渲染)。</summary>
        bool IsShuttingDown { get; }

        /// <summary>排到 UI 线程执行(总是异步排队,不在当前调用栈里直接跑)。</summary>
        void Post(Action action, HostPostPriority priority);

        /// <summary>宿主控件已挂进可视树就立即执行,否则等第一次挂进去时执行一次。UI 线程调用。</summary>
        void WhenLoaded(Action action);

        /// <summary>把宿主的鼠标 / 触摸 / 键盘输入接到指针路由器上;返回值 Dispose 时断开。</summary>
        IDisposable CreatePointerInput(PointerRouter router, PointerRegionStore store, Func<DataBlackboard?> board,
            Func<HevoRect> plotArea, Func<double> dragThreshold);
    }
}
