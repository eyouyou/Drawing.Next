using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Hevo.Charting.Core;
using Hevo.Charting.Features;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 万能联动头的 Avalonia 外壳(对应 WPF 的 UniversalHeaderFeature):取数 / 选根 / 格式化在核心的
    /// <see cref="UniversalHeaderFeatureBase"/>,这里只管控件。排版跟 WPF 版一致:图上方一行可换行的
    /// "圆点 名称: 值",值白色加粗、名称灰色。
    /// </summary>
    public class AvaloniaUniversalHeaderFeature : UniversalHeaderFeatureBase, IAvaloniaDecorator
    {
        private static readonly IBrush s_secondary = new SolidColorBrush(Color.FromRgb(160, 160, 160));

        private WrapPanel _headerContainer = null!;
        // 控件池:每个字段一组(值文本 + 圆点),热路径只改 Text / Fill
        private readonly List<(TextBlock Value, Ellipse Dot)> _items = new();

        public Control Decorate(Control inner)
        {
            var dock = new DockPanel { LastChildFill = true };
            _headerContainer = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 8, 8, 0),
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(_headerContainer, Dock.Top);
            dock.Children.Add(_headerContainer);
            dock.Children.Add(inner);
            return dock;
        }

        /// <summary>测试用:当前字段的(名称, 值)。</summary>
        internal IEnumerable<(string Name, string Value)> Snapshot()
        {
            foreach (var child in _headerContainer.Children)
            {
                if (child is not StackPanel { Children: [_, TextBlock title, _, TextBlock value] }) continue;
                yield return (title.Text ?? string.Empty, value.Text ?? string.Empty);
            }
        }

        /// <summary>测试用:第 <paramref name="index"/> 个字段名和值的前景画刷。</summary>
        internal (IBrush? Label, IBrush? Value) ForegroundsOf(int index)
        {
            var panel = (StackPanel)_headerContainer.Children[index];
            return (((TextBlock)panel.Children[1]).Foreground, ((TextBlock)panel.Children[3]).Foreground);
        }

        protected override void UpdateField(int index, string text, IHevoBrush? dotBrush)
        {
            var (value, dot) = _items[index];
            value.Text = text;
            if (dotBrush != null) AvaloniaBrushes.Bind(dot, Shape.FillProperty, dotBrush);
        }

        protected override void RebuildFields(IReadOnlyList<UniversalHeaderField> fields)
        {
            _headerContainer.Children.Clear();
            _items.Clear();

            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i].Meta;

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 16, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                AvaloniaBrushes.Bind(dot, Shape.FillProperty, f.GetDefaultBrush());

                var title = new TextBlock { Foreground = s_secondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                AvaloniaBrushes.BindText(title, f.Name);
                var colon = new TextBlock { Text = ": ", Foreground = s_secondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var value = new TextBlock
                {
                    Text = "--",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                };
                if (LabelBrush != null)
                {
                    AvaloniaBrushes.Bind(title, TextBlock.ForegroundProperty, LabelBrush);
                    AvaloniaBrushes.Bind(colon, TextBlock.ForegroundProperty, LabelBrush);
                }
                if (ValueBrush != null) AvaloniaBrushes.Bind(value, TextBlock.ForegroundProperty, ValueBrush);

                panel.Children.Add(dot);
                panel.Children.Add(title);
                panel.Children.Add(colon);
                panel.Children.Add(value);
                _headerContainer.Children.Add(panel);
                _items.Add((value, dot));
            }
        }
    }
}
