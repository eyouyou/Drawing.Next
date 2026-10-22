using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hevo.Charting.Features
{
    internal class HeaderItemCache
    {
        public TextBlock ValueBlock { get; }
        public Ellipse ColorDot { get; }

        public HeaderItemCache(TextBlock valueBlock, Ellipse colorDot)
        {
            ValueBlock = valueBlock; ColorDot = colorDot;
        }
    }

    /// <summary>
    /// 万能联动头的 WPF 外壳:取数 / 选根 / 格式化在核心的 <see cref="UniversalHeaderFeatureBase"/>,这里只管 WPF 控件。
    /// <para>
    /// <b>典型用法</b>:
    /// <code>
    /// env.SetupUniversalHeader(hitPort: hitPort);
    /// // 之后凡是带 MetaTrait + DoubleSeriesDataTrait 的 Layer 都会自动出现在头部行。
    /// </code>
    /// </para>
    /// </summary>
    public class UniversalHeaderFeature : UniversalHeaderFeatureBase, IWpfDecorator
    {
        // 头部容器(WrapPanel),由 Decorate 在挂载阶段一次性创建并贴在外层 DockPanel 顶部。
        private WrapPanel _headerContainer = null!;
        // UI 控件池:每个字段对应一组 (TextBlock + Ellipse),热路径只改 Text/Fill,绝不 new 控件。
        private readonly List<HeaderItemCache> _itemCaches = new();

        public UIElement Decorate(UIElement inner)
        {
            var grid = new DockPanel() { LastChildFill = true};

            _headerContainer = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 8, 8, 0),
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top
            };

            grid.Children.Add(_headerContainer);
            DockPanel.SetDock(_headerContainer, Dock.Top);

            grid.Children.Add(inner);
            DockPanel.SetDock(inner, Dock.Top);

            return grid;
        }

        protected override void UpdateField(int index, string text, IHevoBrush? dotBrush)
        {
            var cache = _itemCaches[index];
            cache.ValueBlock.Text = text;
            if (dotBrush != null) cache.ColorDot.BindHevo(Shape.FillProperty, dotBrush);
        }

        protected override void RebuildFields(IReadOnlyList<UniversalHeaderField> fields)
        {
            _headerContainer.Children.Clear();
            _itemCaches.Clear();

            var secondaryBrush = new SolidColorBrush(Color.FromRgb(160, 160, 160));

            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i].Meta;

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 16, 4),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                dot.BindHevo(Shape.FillProperty, f.GetDefaultBrush());

                var title = new TextBlock
                {
                    Foreground = secondaryBrush,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                title.BindHevo(TextBlock.TextProperty, f.Name);

                var colon = new TextBlock
                {
                    Text = ": ",
                    Foreground = secondaryBrush,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var val = new TextBlock
                {
                    Text = "--",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                };
                if (LabelBrush != null)
                {
                    title.BindHevo(TextBlock.ForegroundProperty, LabelBrush);
                    colon.BindHevo(TextBlock.ForegroundProperty, LabelBrush);
                }
                if (ValueBrush != null) val.BindHevo(TextBlock.ForegroundProperty, ValueBrush);

                panel.Children.Add(dot);
                panel.Children.Add(title);
                panel.Children.Add(colon);
                panel.Children.Add(val);

                _headerContainer.Children.Add(panel);
                _itemCaches.Add(new HeaderItemCache(val, dot));
            }
        }
    }
}
