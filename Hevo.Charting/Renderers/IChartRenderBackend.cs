using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Windows;

namespace Hevo.Charting.Renderers
{
    /// <summary>
    /// 可替换的上屏后端(<see cref="ChartCell.RenderBackend"/>)。
    /// null(默认)= WPF DrawingVisual 回放(含 <see cref="RenderMode.Bitmap"/> 位图模式)。
    /// 挂上后 ChartCell 把两块 WPF 矢量画布折叠,改由后端返回的宿主元素上屏;控件层(Widget)仍走 WPF。
    /// 图层录制(Feature 投影 / OnUpdate / DrawCmd 缓冲)不受影响,后端只负责"指令 → 像素"。
    /// 所有方法都在 UI 线程调用。
    /// <para>
    /// 所有权:通过 <see cref="ChartCell.RenderBackend"/> 挂上的后端由 ChartCell 负责释放 ——
    /// 被替换 / 置空、或 <see cref="ChartCell.Shutdown"/> 时,cell 先调 <see cref="Detach"/> 再调 Dispose;
    /// cell 卸载(Unloaded)只是暂停,不释放。调用方不要再 Dispose 已挂上的后端。
    /// 一个后端实例同一时间只能挂在一个 cell 上,挂到第二个 cell 时 <see cref="Attach"/> 应抛 InvalidOperationException。
    /// Detach / Dispose 需可重复调用。
    /// </para>
    /// </summary>
    public interface IChartRenderBackend : IDisposable
    {
        /// <summary>
        /// 挂到 cell 上。返回的元素由 ChartCell 放在矢量层位置(控件层与 loading 蒙层之下),不参与命中测试。
        /// 后端应在这里把各图层现有的前台指令录一遍 —— 挂上之后只会收到脏图层。
        /// </summary>
        UIElement Attach(ChartCell cell);

        /// <summary>
        /// 每帧上屏:图层已 SwapBuffer、尚未清脏标记。两组图层各自按 Z 序排列,
        /// <see cref="ChartLayer.IsDirty"/> 为 true 的图层前台 <see cref="ChartLayer.Buffer"/> 是本帧新指令。
        /// staticLayers 对应 WPF 路径的静态画布(网格 / 轴 / 序列),overlayLayers 对应交互画布(十字光标等高频层)。
        /// </summary>
        void Present(IReadOnlyList<ChartLayer> staticLayers, IReadOnlyList<ChartLayer> overlayLayers);

        /// <summary>图层被移除(随后会再调一次 <see cref="Present"/>,列表里已不含该图层)。</summary>
        void OnLayerRemoved(ChartLayer layer);

        /// <summary>从 cell 摘下(换后端 / 切回 WPF)。宿主元素由 ChartCell 移出可视树。</summary>
        void Detach();
    }
}
