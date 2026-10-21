#if DEBUG
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Hevo.Charting.Core;
using Hevo.Charting.DevTools;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// Avalonia 版拓扑监控:上面是数据流向图(数据源 → 端口 → Feature → 图层,线宽按命中次数),下面是关键事件时间线。
    /// 点节点高亮它的完整上下游,底栏显示该节点的最近值 / 耗时。
    /// <para>
    /// 跟 WPF 的 TopologyInspectorControl 同一份数据(核心的 <see cref="TopologyScan"/> + <see cref="TopologyTracer"/>)、
    /// 同一个分层布局(LayeredGraphLayout);WPF 版另有的端口折叠、Ctrl+E 单帧流向捕获、拖动钉住节点、内存迷你图这里没做。
    /// </para>
    /// </summary>
    internal sealed class AvaloniaTopologyMonitor : DockPanel
    {
        private readonly TopologyGraphView _graph;
        private readonly TextBlock _detail;
        private readonly TextBox _timeline;
        private readonly DispatcherTimer _timer;
        private int _eventCount = -1;

        public AvaloniaTopologyMonitor(AvaloniaChartCell cell)
        {
            _graph = new TopologyGraphView(cell);
            _detail = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 12,
                Margin = new Thickness(8, 4),
                TextWrapping = TextWrapping.Wrap,
                Text = "点节点查看上下游;每 500ms 刷新一次。",
            };
            _timeline = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                Height = 160,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                BorderThickness = new Thickness(0),
            };
            SetDock(_timeline, Dock.Bottom);
            SetDock(_detail, Dock.Bottom);
            Children.Add(_timeline);
            Children.Add(_detail);
            Children.Add(new ScrollViewer
            {
                Content = _graph,
                HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            });

            _graph.SelectionChanged += () => _detail.Text = _graph.DescribeSelection();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Refresh();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Refresh();
            _timer.Start();
        }

        // 关窗后停表:定时器持有本控件,不停的话监控窗关了还在每 500ms 采样
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _timer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        internal void Refresh()
        {
            _graph.Refresh();
            if (_graph.SelectedId != null) _detail.Text = _graph.DescribeSelection();
            var tracer = _graph.Tracer;
            if (tracer == null)
            {
                _timeline.Text = "当前 schema 没有追踪器(只在 Debug 构建、schema 装配后才有)。";
                return;
            }
            var events = tracer.DumpKeyEvents();
            if (events.Length == _eventCount) return;
            _eventCount = events.Length;
            var sb = new StringBuilder();
            double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            for (int i = events.Length - 1; i >= 0 && i >= events.Length - 200; i--)
            {
                var ev = events[i];
                sb.Append((ev.TicksFromStart * tickMs).ToString("F1", CultureInfo.InvariantCulture).PadLeft(10)).Append("ms  ")
                  .Append(TopologyTracer.EventKindShort(ev.Kind).PadRight(8)).Append(' ')
                  .Append(_graph.LabelOf(ev.TargetId));
                if (!string.IsNullOrEmpty(ev.Summary)) sb.Append("  ").Append(ev.Summary);
                sb.AppendLine();
            }
            _timeline.Text = sb.ToString();
        }

        internal TopologyGraphView Graph => _graph;
    }

    /// <summary>流向图本体:节点 / 连线来自静态扫描 + 运行时命中,布局交给核心的分层布局,画用 Avalonia DrawingContext。</summary>
    internal sealed class TopologyGraphView : Control
    {
        private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
        private static readonly IBrush NodeBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x36));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        private static readonly IPen NodeBorder = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)), 1);
        private static readonly IPen SelectedBorder = new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 2);
        private static readonly Color EdgeColor = Color.FromRgb(0x47, 0x55, 0x69);
        private static readonly Color HotEdgeColor = Color.FromRgb(0x38, 0xBD, 0xF8);
        // 类别色条:源头 / 端口 / Feature / 图层
        private static readonly IBrush[] TierBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
            new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)),
        };
        private static readonly Typeface Font = new(FontFamily.Default);

        private readonly AvaloniaChartCell _cell;
        private readonly Dictionary<string, TopologyNodeInfo> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Src, string Tgt), int> _links = new();
        private LayeredGraphLayout.Result? _layout;
        private int _layoutSignature;
        private Size _layoutSize;
        private readonly HashSet<string> _related = new(StringComparer.Ordinal);

        public TopologyGraphView(AvaloniaChartCell cell)
        {
            _cell = cell;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public event Action? SelectionChanged;

        public string? SelectedId { get; private set; }

        /// <summary>当前 schema 的追踪器(每次取:监控窗寿命可能跨过 schema 替换)。</summary>
        public TopologyTracer? Tracer => TracerRegistry.Get(_cell.Template!);

        internal IReadOnlyDictionary<string, TopologyNodeInfo> Nodes => _nodes;
        internal IReadOnlyDictionary<(string Src, string Tgt), int> Links => _links;

        public string LabelOf(string id)
        {
            if (_nodes.TryGetValue(id, out var n)) return n.Label;
            var tracer = Tracer;
            if (tracer != null && tracer.NodeMetadata.TryGetValue(id, out var meta)) return meta.Name;
            return id;
        }

        /// <summary>重新采样:静态骨架 + 追踪到的连线 + Feature → 图层 终点。拓扑变了才重排。</summary>
        public void Refresh()
        {
            _nodes.Clear();
            _links.Clear();
            if (_cell.Template != null)
            {
                foreach (var n in TopologyScan.ScanNodes(_cell)) _nodes[n.Id] = n;
            }

            var tracer = Tracer;
            if (tracer != null)
            {
                tracer.SampleActivity();
                foreach (var kv in tracer.LinkHits)
                {
                    var (src, tgt) = kv.Key;
                    AddNodeFromTracer(tracer, src);
                    AddNodeFromTracer(tracer, tgt);
                    if (_nodes.ContainsKey(src) && _nodes.ContainsKey(tgt)) _links[(src, tgt)] = kv.Value;
                }
            }
            AppendLayerEdges(tracer);

            int sig = Signature();
            if (sig != _layoutSignature || _layout == null)
            {
                _layoutSignature = sig;
                _layout = null;
                InvalidateMeasure();
            }
            if (SelectedId != null && !_nodes.ContainsKey(SelectedId)) Select(null);
            InvalidateVisual();
        }

        private void AddNodeFromTracer(TopologyTracer tracer, string id)
        {
            if (_nodes.ContainsKey(id)) return;
            if (tracer.NodeMetadata.TryGetValue(id, out var meta)) _nodes[id] = new TopologyNodeInfo(id, meta.Name, meta.Tier);
        }

        // 图层不读写黑板,追踪器看不到;从 Feature 挂着的图层补终点节点,流量用该 Feature 的 OnProject 次数(同 WPF 版)
        private void AppendLayerEdges(TopologyTracer? tracer)
        {
            if (_cell.Template is not ReactiveSchema rs) return;
            foreach (var f in rs.ListFeatures())
            {
                var layers = f.ManagedLayers;
                if (layers.Count == 0) continue;
                var fid = TopologyScan.FeatureId(f);
                if (!_nodes.ContainsKey(fid)) continue;
                int hits = tracer != null && tracer.FeatureCost.TryGetValue(fid, out var cost) ? cost.Samples : 0;
                foreach (var layer in layers)
                {
                    var lid = $"L_{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(layer)}";
                    string name = (layer.Name ?? "").TrimStart('_');
                    if (name.Length == 0) name = layer.GetType().Name;
                    _nodes[lid] = new TopologyNodeInfo(lid, name, 3);
                    _links[(fid, lid)] = hits;
                }
            }
        }

        private int Signature()
        {
            var h = new HashCode();
            foreach (var id in _nodes.Keys.OrderBy(k => k, StringComparer.Ordinal)) h.Add(id);
            foreach (var l in _links.Keys.OrderBy(k => k.Src, StringComparer.Ordinal).ThenBy(k => k.Tgt, StringComparer.Ordinal)) h.Add(l);
            return h.ToHashCode();
        }

        private LayeredGraphLayout.Result EnsureLayout(Size available)
        {
            if (_layout != null && _layoutSize == available) return _layout;
            var nodes = _nodes.Values.Select(n => new LayeredGraphLayout.Node
            {
                Id = n.Id,
                Height = 28,
                // 数据源 / 传感器钉在第一层,读起来从左往右
                PinFirstLayer = n.Tier == 0,
            }).ToList();
            var opt = new LayeredGraphLayout.Options
            {
                Left = 16,
                Top = 16,
                Width = Math.Max(600, available.Width - 32),
                Height = Math.Max(400, available.Height - 32),
            };
            _layout = LayeredGraphLayout.Compute(nodes, _links.Keys, opt);
            _layoutSize = available;
            return _layout;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = new Size(double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width,
                                double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
            var layout = EnsureLayout(size);
            return new Size(Math.Max(size.Width, layout.ContentWidth + 32), Math.Max(size.Height, layout.ContentHeight + 32));
        }

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(BgBrush, new Rect(Bounds.Size));
            var layout = _layout;
            if (layout == null) return;
            long maxHits = _links.Count == 0 ? 1 : Math.Max(1, _links.Values.Max());

            foreach (var kv in _links)
            {
                if (!layout.Boxes.TryGetValue(kv.Key.Src, out var a) || !layout.Boxes.TryGetValue(kv.Key.Tgt, out var b)) continue;
                bool hot = SelectedId != null && _related.Contains(kv.Key.Src) && _related.Contains(kv.Key.Tgt);
                double width = 1 + 3 * Math.Log(1 + kv.Value) / Math.Log(1 + maxHits);
                var pen = new Pen(new SolidColorBrush(hot ? HotEdgeColor : EdgeColor), width,
                    dashStyle: layout.ReversedEdges.Contains(kv.Key) ? DashStyle.Dash : null);
                if (layout.Routes.TryGetValue(kv.Key, out var route) && route.Count >= 2)
                {
                    for (int i = 1; i < route.Count; i++)
                        dc.DrawLine(pen, new Point(route[i - 1].X, route[i - 1].Y), new Point(route[i].X, route[i].Y));
                }
                else
                {
                    dc.DrawLine(pen, new Point(a.X + a.W, a.Y + a.H / 2), new Point(b.X, b.Y + b.H / 2));
                }
                if (hot) DrawText(dc, kv.Value.ToString(CultureInfo.InvariantCulture), new Point(b.X - 34, b.Y - 13), 10, DimTextBrush);
            }

            foreach (var n in _nodes.Values)
            {
                if (!layout.Boxes.TryGetValue(n.Id, out var box)) continue;
                var rect = new Rect(box.X, box.Y, box.W, box.H);
                bool dim = SelectedId != null && !_related.Contains(n.Id);
                using (dc.PushOpacity(dim ? 0.35 : 1))
                {
                    dc.DrawRectangle(NodeBrush, n.Id == SelectedId ? SelectedBorder : NodeBorder, rect, 4, 4);
                    dc.FillRectangle(TierBrushes[Math.Clamp(n.Tier, 0, TierBrushes.Length - 1)], new Rect(rect.X, rect.Y, 3, rect.Height));
                    using (dc.PushClip(rect.Deflate(new Thickness(8, 0, 4, 0))))
                        DrawText(dc, n.Label, new Point(rect.X + 9, rect.Y + (rect.Height - 15) / 2), 12, TextBrush);
                }
            }
        }

        private static void DrawText(DrawingContext dc, string text, Point at, double size, IBrush brush)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font, size, brush);
            dc.DrawText(ft, at);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var p = e.GetPosition(this);
            Select(HitTestNode(p));
            e.Handled = true;
        }

        internal string? HitTestNode(Point p)
        {
            if (_layout == null) return null;
            foreach (var kv in _layout.Boxes)
            {
                var b = kv.Value;
                if (_nodes.ContainsKey(kv.Key) && new Rect(b.X, b.Y, b.W, b.H).Contains(p)) return kv.Key;
            }
            return null;
        }

        /// <summary>选中节点(null 取消):沿去掉回边后的前驱 / 后继求完整上下游。</summary>
        internal void Select(string? id)
        {
            SelectedId = id;
            _related.Clear();
            if (id != null && _layout != null)
            {
                Walk(id, _layout.Succ);
                Walk(id, _layout.Pred);
            }
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }

        private void Walk(string start, Dictionary<string, List<string>> next)
        {
            var stack = new Stack<string>();
            stack.Push(start);
            _related.Add(start);
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!next.TryGetValue(cur, out var list)) continue;
                foreach (var n in list)
                {
                    if (!seen.Add(n)) continue;
                    _related.Add(n);
                    stack.Push(n);
                }
            }
        }

        public string DescribeSelection()
        {
            if (SelectedId == null) return "点节点查看上下游;每 500ms 刷新一次。";
            var sb = new StringBuilder();
            sb.Append(LabelOf(SelectedId)).Append("  [").Append(SelectedId).Append(']');
            var tracer = Tracer;
            if (tracer != null)
            {
                if (tracer.LastValues.TryGetValue(SelectedId, out var v))
                    sb.Append("  最近值(").Append(v.TypeName).Append("): ").Append(v.Value?.ToString() ?? "null");
                if (tracer.FeatureCost.TryGetValue(SelectedId, out var cost) && cost.Samples > 0)
                {
                    double avgUs = cost.TotalTicks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / cost.Samples;
                    sb.Append("  OnProject ").Append(cost.Samples).Append(" 次,平均 ").Append(avgUs.ToString("F1", CultureInfo.InvariantCulture)).Append("µs");
                }
            }
            int up = _links.Keys.Count(k => k.Tgt == SelectedId), down = _links.Keys.Count(k => k.Src == SelectedId);
            sb.Append("  入边 ").Append(up).Append(" / 出边 ").Append(down);
            return sb.ToString();
        }
    }
}
#endif
