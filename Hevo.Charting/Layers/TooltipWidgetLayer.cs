using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Renderers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hevo.Charting.Layers
{
    public partial class TooltipWidgetLayer : ChartLayer
    {
        private readonly Border _widgetContainer;
        private readonly StackPanel _panel;

        // ==========================================
        // 💥 极限性能：UI 控件池 (Object Pool)
        // 彻底杜绝在 OnUpdate 里 new 任何 UI 元素！
        // ==========================================
        private class UIRowCache
        {
            public StackPanel Container { get; } = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            public Ellipse ColorDot { get; } = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            public TextBlock TitleBlock { get; } = new TextBlock { Foreground = Brushes.LightGray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            public TextBlock ValueBlock { get; } = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

            public UIRowCache()
            {
                Container.Children.Add(ColorDot);
                Container.Children.Add(TitleBlock);
                Container.Children.Add(ValueBlock);
            }
        }

        private readonly List<UIRowCache> _uiPool = new(16); // 预分配池

        public TooltipWidgetLayer()
        {
            // Tooltip 跟着鼠标动属于高频交互,Level 设 Interaction 让 ChartCell 路由到 _overlayCanvas,
            // 不要污染 _drawingCanvas (那里挂着 CandleLayer 这种 N 大但低频的内容,需要 BitmapCache 隔离)。
            Level = ChartLayerType.Interaction;

            _panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };

            _widgetContainer = new Border
            {
                Child = _panel,
                CornerRadius = new CornerRadius(6),
                IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), // 用微发光的边框替代阴影
                BorderThickness = new Thickness(1),
                // 💥 救命黑科技 1：强制 WPF 在 Measure 阶段就将尺寸取整到物理像素！
                UseLayoutRounding = true,
                // 💥 救命黑科技 2：强迫边缘对齐设备像素网格，绝不产生虚边和溢出截断！
                SnapsToDevicePixels = true
            };
        }

        // OnUpdate 直接改 Border / TextBlock 等 WPF 控件,必须在 UI 线程。
        protected internal override bool RequiresUiThread => true;

        private Size _cachedSize = Size.Empty;
        private HevoPoint _lastAnchor = new HevoPoint(-1f, -1f); // 💥 使用 HevoPoint

        // 💥 严格遵守渲染管线：极速复用与排版
        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            var trait = data.Get<TooltipWidgetTrait>();

            // 如果数据为空或隐藏，直接把容器移出视口即可 (或者发空的 bounds)
            if (trait == null || !trait.IsVisible || trait.Rows.Length == 0) return;

            // 1. 设置背景色 (走注册表静态解析，因为 Widget 自身不参与 DynamicResource 树绑定)
            _widgetContainer.Background = WpfRenderRegistry.CreateBrush(trait.Background) ?? Brushes.DarkGray;

            var span = trait.Rows.Span;

            // ==========================================
            // 💥 2. UI 控件极速池化复用
            // ==========================================
            // 确保池子里的控件足够多
            while (_uiPool.Count < span.Length)
            {
                var newRow = new UIRowCache();
                _uiPool.Add(newRow);
                _panel.Children.Add(newRow.Container);
            }

            // 更新数据并控制可见性
            for (int i = 0; i < _uiPool.Count; i++)
            {
                var ui = _uiPool[i];
                if (i < span.Length)
                {
                    var rowData = span[i];
                    ui.Container.Visibility = Visibility.Visible;

                    // 💥 多态解析：将 IHevoString 解析为物理字符
                    ui.TitleBlock.Text = WpfRenderRegistry.ResolveString(rowData.Name) + ":";
                    ui.ValueBlock.Text = rowData.Value;

                    // 颜色解析
                    if (rowData.ValueBrush != null)
                        ui.ColorDot.Fill = WpfRenderRegistry.CreateBrush(rowData.ValueBrush);
                }
                else
                {
                    // 隐藏多余的控件，不从视觉树中 Remove，避免重排开销！
                    ui.Container.Visibility = Visibility.Collapsed;
                }
            }

            // ==========================================
            // 💥 3. 核心黑科技：强制提前测量 (Measure) 悬浮窗尺寸
            // ==========================================
            // 告诉 WPF 容器：“假设你有无限大的空间，请告诉我你想要多大尺寸？”
            if (_cachedSize == Size.Empty || Math.Abs(trait.AnchorPos.X - _lastAnchor.X) > 1.0f)
            {
                _widgetContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _cachedSize = _widgetContainer.DesiredSize;
                _lastAnchor = trait.AnchorPos;
            }

            // 💥 从纯 float 数学域获取左上角位置
            HevoPoint topLeft = TooltipPositionCalculator.Calc(
                anchor: trait.AnchorPos,
                width: (float)_cachedSize.Width,
                height: (float)_cachedSize.Height,
                plotArea: trait.PlotArea,
                mode: trait.PositionMode,
                offset: trait.Offset
            );

            // 提交给 Widget 队列 (这里与 WPF 进行最后一次物理映射交接)
            var bounds = new Rect(topLeft.X, topLeft.Y, _cachedSize.Width, _cachedSize.Height);
            widgetSink.UpdateLayout(bounds, _widgetContainer);
        }
    }
}
