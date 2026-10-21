using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Features;
using Hevo.Charting.Layers;
using Hevo.Charting.Renderers;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 悬浮窗图层的 Avalonia 版(对应 WPF 的 TooltipWidgetLayer):吃 <see cref="TooltipWidgetTrait"/>,
    /// 用 Avalonia 控件排版,经控件缓冲交给 <see cref="AvaloniaChartCell"/> 的控件层摆放。
    /// </summary>
    public partial class AvaloniaTooltipLayer : ChartLayer
    {
        private static readonly IBrush s_fallbackBackground = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30));

        private readonly Border _widgetContainer;
        private readonly StackPanel _panel;

        // 行控件池:OnUpdate 只改文字 / 显隐,不新建控件(行数只增不减)
        private sealed class RowCache
        {
            public StackPanel Container { get; } = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            public Ellipse ColorDot { get; } = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            public TextBlock TitleBlock { get; } = new() { Foreground = Brushes.LightGray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            public TextBlock ValueBlock { get; } = new() { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeight.Bold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

            public RowCache()
            {
                Container.Children.Add(ColorDot);
                Container.Children.Add(TitleBlock);
                Container.Children.Add(ValueBlock);
            }
        }

        private readonly List<RowCache> _rows = new(16);

        public AvaloniaTooltipLayer()
        {
            // 跟着鼠标高频变化:进交互层,不让静态层(K 线)跟着重画
            Level = ChartLayerType.Interaction;

            _panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10) };
            _widgetContainer = new Border
            {
                Child = _panel,
                CornerRadius = new CornerRadius(6),
                IsHitTestVisible = false,
                Background = s_fallbackBackground,
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                UseLayoutRounding = true,
            };
        }

        // OnUpdate 直接改 Avalonia 控件,只能在 UI 线程录制
        protected internal override bool RequiresUiThread => true;

        private Size _cachedSize;
        private bool _hasSize;
        private HevoPoint _lastAnchor = new(-1f, -1f);

        protected override void OnUpdate(IVisualData data, IDrawingSink drawSink, WidgetBuffer widgetSink)
        {
            var trait = data.Get<TooltipWidgetTrait>();
            if (trait == null || !trait.IsVisible || trait.Rows.Length == 0) return;

            _widgetContainer.Background = AvaloniaBrushes.Create(trait.Background) ?? s_fallbackBackground;

            var span = trait.Rows.Span;
            while (_rows.Count < span.Length)
            {
                var row = new RowCache();
                _rows.Add(row);
                _panel.Children.Add(row.Container);
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                var ui = _rows[i];
                if (i < span.Length)
                {
                    var rowData = span[i];
                    ui.Container.IsVisible = true;
                    ui.TitleBlock.Text = HevoStrings.Resolve(rowData.Name, AvaloniaHostRegistration.Services) + ":";
                    ui.ValueBlock.Text = rowData.Value;
                    if (rowData.ValueBrush != null) ui.ColorDot.Fill = AvaloniaBrushes.Create(rowData.ValueBrush);
                }
                else
                {
                    ui.Container.IsVisible = false;
                }
            }

            // 尺寸只在锚点横向挪动超过 1px 时重测(同 WPF 版:竖向移动行数 / 文本宽度基本不变)
            if (!_hasSize || Math.Abs(trait.AnchorPos.X - _lastAnchor.X) > 1.0f)
            {
                _widgetContainer.Measure(Size.Infinity);
                _cachedSize = _widgetContainer.DesiredSize;
                _hasSize = true;
                _lastAnchor = trait.AnchorPos;
            }

            HevoPoint topLeft = TooltipPositionCalculator.Calc(
                anchor: trait.AnchorPos,
                width: (float)_cachedSize.Width,
                height: (float)_cachedSize.Height,
                plotArea: trait.PlotArea,
                mode: trait.PositionMode,
                offset: trait.Offset);

            widgetSink.UpdateLayout(new HevoRect(topLeft.X, topLeft.Y, (float)_cachedSize.Width, (float)_cachedSize.Height), _widgetContainer);
        }
    }
}
