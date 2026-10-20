using Hevo.Charting.Core;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hevo.Charting.DevTools
{
    // ==========================================
    // 💥 4. 终极 UI 控制器 (高级引脚梳理 + 点击聚焦)
    // ==========================================
    public class TopologyInspectorControl : Panel
    {
        private readonly TopologyTracer? _tracer;
        // 保留 cell 引用而不是 schema —— Inspector 寿命可能跨过 schema 替换(蓝图 reload / 主图 unload),
        // 每次 dump 时从 cell.Template 取**当前**的 schema,才能拿到活的 board 和最新 values。
        // _schemaAtConstruction 仅用于 _meta 段标记 schemaReplaced,提示用户上下文已切换。
        private readonly ChartCell _cell;
        private readonly ReactiveSchema? _schemaAtConstruction;
        private readonly DrawingVisual _visual = new();
        // 模型:静态扫描 + 运行时追踪到的全部节点 / 连线(含 Feature → 图层 终点)。
        private List<TopoNode> _modelNodes = new();
        private readonly List<TopoLink> _modelLinks = new();
        // 视图:当前要画的节点 / 连线,每帧由 RebuildView 从模型生成。
        // 完整视图 = 模型;折叠视图把"有人写也有人读"的端口收进 Feature → Feature 的边里。
        private readonly List<TopoNode> _nodes = new();
        private readonly List<TopoLink> _links = new();
        private bool _foldPorts;
        private readonly Dictionary<(string, string), TopoLink> _foldedLinkCache = new();
        private int _viewSignature = int.MinValue;

        // 💥 交互升级：从 Hover 改为 Click 锁定
        private string? _selectedId;


        // 布局交给 LayeredGraphLayout(参照 ELK Layered):按数据流分层、长边拆虚拟节点减少交叉、
        // 长边尽量拉直、连线走横竖折线。以下几张表是布局结果的缓存,视图或画布尺寸变了才重算。
        private LayeredGraphLayout.Result? _layout;
        // 节点 id → 列号。源在最左,连线一律从左往右;一条线都没有的节点单放最右一列。
        private readonly Dictionary<string, int> _columnOf = new();
        private int _flowColumnCount;
        private bool _hasUnlinkedColumn;

        // 分层时摘掉的回边(画成虚线);去掉回边后的前驱 / 后继表,点选节点时沿它求完整上下游。
        private readonly HashSet<(string Src, string Tgt)> _backEdges = new();
        private readonly Dictionary<string, List<string>> _fwdSucc = new();
        private readonly Dictionary<string, List<string>> _fwdPred = new();


        // 视觉规范:slate 深色系(背景 / 卡片 / 边框三级明度),颜色只留给语义(类别色条、选中上下游、回写),
        // 标签用 Segoe UI,数字(耗时、流量)用等宽字体。以前全是青色霓虹描边 + 常亮光圈,整张图像在报警。
        private readonly Brush _bgBrush = Frozen(255, 15, 23, 42);      // #0F172A
        private readonly Brush _nodeBrush = Frozen(255, 27, 35, 54);    // #1B2336 卡片
        private readonly Typeface _font = new Typeface("Segoe UI");
        private readonly Typeface _fontStrong = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private readonly Typeface _monoFont = new Typeface("Consolas");

        // 持有所属 Window 的引用,Unloaded 时反订阅 PreviewKeyDown,避免 Inspector 关闭后还吃事件。
        private Window? _hostWindow;

        public TopologyInspectorControl(ChartCell cell)
        {
            this.Focusable = true;
            this.Cursor = Cursors.Hand; // 提示可点击
            this.Background = Brushes.Transparent;
            this.AddVisualChild(_visual);

            _cell = cell;
            _schemaAtConstruction = cell.Template as ReactiveSchema;

            // Ctrl+E 走 Window 级 PreviewKeyDown,跳过 Panel 焦点问题 ——
            // Panel 即便 Focusable=true,点击也不会自动转移键盘焦点(WPF 默认行为),
            // 之前 OnKeyDown 永远收不到 Ctrl+E。挂到 Window 上,焦点在哪都能触发。
            this.Loaded += OnInspectorLoaded;
            this.Unloaded += OnInspectorUnloaded;

#if DEBUG
            _tracer = TracerRegistry.Get(cell.Template);

            var staticNodes = TopologyScanner.Scan(cell);
            _modelNodes = staticNodes;

            // 把静态扫描出的节点(尤其是 port)登记进 NodeMetadata —— 后续 friendly name map
            // (BuildFeatureNameMap / BuildPortDisplayMap)从 NodeMetadata 取数据。
            // 没这一步,只有运行时被 Read/Write 过的端口才进 NodeMetadata,静态 scan 出来但还没动数据的 port
            // 在画布上显示原始 Label,用户右键开 inspector 那一瞬看到的就是 ugly 名字。
            if (_tracer != null)
            {
                foreach (var n in staticNodes)
                    _tracer.NodeMetadata.TryAdd(n.Id, (n.Label, n.Tier));

                // 记录 port → 声明它的 owner —— 给孤儿 port(declared 但没读写)在
                // BuildPortDisplayMap 里加 "Owner › PortName" 前缀,用户一眼能看出这根 port 是谁的。
                //
                // 顺序:schema 自有 port 必须**先注册**(TryAdd 是 no-op-on-conflict)。
                // ChartFeature.Viewport 字段透传 schema 的 ViewportPorts,任何 feature 通过 ExtractPorts
                // 都能"看到" Viewport.UserRange / ActiveRange / LogicalLength —— 第一个被处理的 feature
                // 会冒充成 Viewport.* 的 owner,而 Viewport.* 实际归 schema。所以先 schema 锁定 Viewport.* / LocalHit,
                // 再让 features 认领它们独有的端口。
                if (cell.Template is ReactiveSchema rs)
                {
                    // (a) schema 自有 port —— LocalHit / Viewport.* 由 SchemaContext 持有,不属任何 feature。
                    // owner 用合成 id "F_Schema",NodeMetadata 里也补一条 ("Schema", Tier 2),featureNameMap
                    // 拿这个 id 直接显示 "Schema"。LocalHit 在 standalone 模式下显示成 "Schema › LocalHit (孤)" ——
                    // 用户知道它是 schema 自带 hit 桥,只在 dashboard 联动才被覆盖共享。
                    const string SchemaOwnerId = "F_Schema";
                    _tracer.NodeMetadata.TryAdd(SchemaOwnerId, ("Schema", 2));
                    foreach (var port in TopologyScanner.ExtractPortsPublic(rs))
                        _tracer.PortOwner.TryAdd(port.Id, SchemaOwnerId);

                    // (b) feature 自有 port —— Viewport.* 已被 (a) 锁了,这里 TryAdd 自动 no-op。
                    foreach (var feature in rs.ListFeatures())
                    {
                        var fid = $"F_{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(feature)}";
                        foreach (var port in TopologyScanner.ExtractPortsPublic(feature))
                            _tracer.PortOwner.TryAdd(port.Id, fid);
                    }
                }
            }
#endif
        }

        // 每帧回调随 Loaded / Unloaded 订退:CompositionTarget.Rendering 是静态事件,
        // 不退订的话监控窗关掉后控件被它一直持有,每帧照样采样 + 重绘。
        private void OnRendering(object? sender, EventArgs e)
        {
#if DEBUG
            if (_tracer != null)
            {
                // 高频活动层:每帧调一次,内部 500ms 限流 —— 把 _portWriteCount 累计差量塞进 ring buffer。
                _tracer.SampleActivity();
                var (dynNodes, dynLinks) = _tracer.DumpTopology();
                AppendLayerEdges(dynNodes, dynLinks);
                MergeDynamicBlood(dynNodes, dynLinks);
                if (_tracer.TryTakeCapture(out var steps)) BuildFlow(steps);
            }
#endif
            Render();
            EndFrameTextCache();
        }

#if DEBUG
        // 图层不读写黑板,追踪器看不到它们;每帧从 schema 当前的 Feature 取挂着的图层,补成终点节点和 Feature → 图层 的边,
        // 图上才能回答"这根数据最后画到了哪"。图层没有自己的命中计数,用 Feature 的 OnProject 次数当流量。
        private void AppendLayerEdges(List<TopoNode> nodes, List<TopoLink> links)
        {
            if (_tracer == null || !(_cell.Template is ReactiveSchema rs)) return;
            foreach (var f in rs.ListFeatures())
            {
                var layers = f.ManagedLayers;
                if (layers.Count == 0) continue;
                var fid = $"F_{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(f)}";
                int hits = _tracer.FeatureCost.TryGetValue(fid, out var cost) ? cost.Samples : 0;
                foreach (var layer in layers)
                {
                    var lid = $"L_{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(layer)}";
                    // 去掉挂载时加的 hex 后缀;没起名的图层(名字只剩后缀)退回类型名
                    string name = FriendlyPortName(layer.Name ?? "").TrimStart('_');
                    if (name.Length == 0 || (name.Length == 8 && IsHex(name, 0, 8))) name = layer.GetType().Name;
                    nodes.Add(new TopoNode { Id = lid, Label = name, Tier = 3 });
                    links.Add(new TopoLink { Src = fid, Tgt = lid, HitCount = hits, ToLayer = true });
                }
            }
        }
#endif

        private void OnInspectorLoaded(object sender, RoutedEventArgs e)
        {
            // Loaded 可能重复触发(重新挂进可视树),先退再订防止叠加
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;

            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
                _hostWindow.PreviewKeyDown += OnHostWindowPreviewKeyDown;
        }

        private void OnInspectorUnloaded(object sender, RoutedEventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;

            if (_hostWindow != null)
            {
                _hostWindow.PreviewKeyDown -= OnHostWindowPreviewKeyDown;
                _hostWindow = null;
            }
        }

        private void OnHostWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 中文输入法开着时字母键先交给 IME,e.Key 是 ImeProcessed,真实按键在 ImeProcessedKey
            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (key == Key.T && Keyboard.Modifiers == ModifierKeys.None)
            {
                _timelineCollapsedByUser = !TimelineCollapsed;
                e.Handled = true;
                return;
            }
#if DEBUG
            if (key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && _tracer != null)
            {
                ClearFlow();
                _selectedId = null;
                _tracer.BeginCapture();
                e.Handled = true;
                return;
            }
            if (key == Key.Escape && (_flow != null || _tracer?.IsCaptureArmed == true))
            {
                _tracer?.CancelCapture();
                ClearFlow();
                e.Handled = true;
                return;
            }
#endif
            if (key == Key.F && Keyboard.Modifiers == ModifierKeys.None)
            {
                _foldPorts = !_foldPorts;
                // 选中的端口在折叠视图里可能被收起,清掉免得高亮落空
                if (_foldPorts && _selectedId != null && !_selectedId.StartsWith("F_", StringComparison.Ordinal)
                    && !_selectedId.StartsWith("L_", StringComparison.Ordinal) && _selectedId != "PIPE")
                    _selectedId = null;
                e.Handled = true;
                return;
            }
            TryHandleSnapshotShortcut(e);
        }

        // Ctrl+E 把当前拓扑(节点+连线+命中数+平均成本)拷到剪贴板 + 写到 Debug 输出,方便贴 issue / diff。
        // 同时保留 OnKeyDown 走焦点路径 —— 万一以后 Panel 真拿到了焦点,这条路径仍然能用。
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            TryHandleSnapshotShortcut(e);
        }

        private void TryHandleSnapshotShortcut(KeyEventArgs e)
        {
#if DEBUG
            if (_tracer != null && e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // 拓扑结构(Tracer 维护)+ port 当前值(Board 维护)+ _meta 诊断段拼成同一份 JSON。
                // Inspector 寿命跨 schema(template 切换),dump 时把 schema/board 状态打出来,
                // 看到 ports=[] 时能直接从 _meta 判断:board 没了 / 桶空 / cast 失败,不用回头加日志。
                var topoJson = _tracer.DumpJson();
                var meta = BuildMetaJson();
                var portsJson = TryDumpPortValuesJson();

                // topoJson 末尾是 "}",在 } 前面塞 _meta + 可选 ports 段。
                var trimmed = topoJson.TrimEnd();
                string json;
                if (trimmed.EndsWith("}"))
                {
                    var body = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
                    var sb = new System.Text.StringBuilder(body.Length + meta.Length + 256);
                    sb.Append(body).Append(",\n  ").Append(meta);
                    if (portsJson != null) sb.Append(",\n  ").Append(portsJson);
                    sb.Append("\n}");
                    json = sb.ToString();
                }
                else
                {
                    json = topoJson; // 防御:格式异常 fallback
                }

                try { Clipboard.SetText(json); } catch { /* clipboard 偶发被独占,忽略 */ }
                Debug.WriteLine("=== Topology Snapshot (also copied to clipboard) ===");
                Debug.WriteLine(json);
                e.Handled = true;
            }
#endif
        }

#if DEBUG
        // 输出 "_meta": { schemaType, schemaHash, boardAvailable, boardDisposed, boardHash, totalEntries, portEntries, nonPortEntries }
        // 当 "ports" 是空数组时,这个段能直接定位是哪一步出问题:
        //   boardAvailable=false → schema._latestBoard 是 null(管线没 push 过 / 不是 ReactiveSchema)
        //   boardAvailable=true & totalEntries=0 → 数据写在别的 board(典型 dashboard 多 board 场景)
        //   totalEntries>0 & portEntries=0 → board 里 key 不是 IDataPort,过滤被筛掉(异常情形)
        private string BuildMetaJson()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("\"_meta\": { ");

            // 每次 dump 都从 cell.Template 拿当前 schema,不依赖 ctor 时缓存的引用。
            // 蓝图 reload 后 cell.Template 是新 schema,values 取新的;旧 schema 还在 _schemaAtConstruction
            // 里,用来标记 schemaReplaced 提示用户拓扑数据(_tracer 是 ctor 时绑的)对应的是上一代 schema。
            var currentSchema = _cell.Template as ReactiveSchema;
            string schemaType = currentSchema?.GetType().Name ?? "null";
            int schemaHash = currentSchema != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(currentSchema) : 0;
            bool replaced = !ReferenceEquals(currentSchema, _schemaAtConstruction);
            sb.Append("\"schemaType\": ").Append(JsonEscape(schemaType))
              .Append(", \"schemaHash\": ").Append(schemaHash)
              .Append(", \"schemaReplaced\": ").Append(replaced ? "true" : "false");

            var board = currentSchema?.CurrentBoard;
            if (board == null)
            {
                sb.Append(", \"boardAvailable\": false");
            }
            else
            {
                int boardHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(board);
                sb.Append(", \"boardAvailable\": true")
                  .Append(", \"boardDisposed\": ").Append(board.IsDisposed ? "true" : "false")
                  .Append(", \"boardHash\": ").Append(boardHash);
                try
                {
                    var (total, port, nonPort) = board.DumpMemoryStats();
                    sb.Append(", \"totalEntries\": ").Append(total)
                      .Append(", \"portEntries\": ").Append(port)
                      .Append(", \"nonPortEntries\": ").Append(nonPort);
                }
                catch (ObjectDisposedException) { sb.Append(", \"statsError\": \"disposed\""); }
            }
            // tracer 这条路独立于 board lifecycle —— 即便 board.IsDisposed,LastValues 仍然有效。
            // ports 段就是从这个字段渲染的,这里把 count 暴露出来辅助 debug。
            int cachedValues = _tracer?.LastValues.Count ?? 0;
            sb.Append(", \"tracerCachedValues\": ").Append(cachedValues);

            sb.Append(" }");
            return sb.ToString();
        }

        // 把当前 port values 序列化成 "\"ports\": [...]" 片段(不含外层 {});
        // tracer 不可达就返回 null。数据走 tracer.LastValues(写入路径上同步缓存),
        // 跟 board lifecycle 完全解耦 —— 蓝图 one-shot 数据源 push 完 board 立即被 dispose 也无妨,
        // 只要 Inspector 还活着就能拿到最后已知 values。
        private string? TryDumpPortValuesJson()
        {
            if (_tracer == null) return null;
            var values = _tracer.LastValues;
            // ConcurrentDictionary 的 ToArray 是快照,迭代期间被并发写也安全。
            var rows = new List<(string Id, string DisplayName, string TypeName, object? Value)>(values.Count);
            foreach (var kv in values)
                rows.Add((kv.Key, kv.Value.DisplayName, kv.Value.TypeName, kv.Value.Value));
            var sb = new System.Text.StringBuilder(256 + rows.Count * 96);
            sb.Append("\"ports\": [");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = rows[i];
                sb.Append("\n    { \"id\": ").Append(JsonEscape(r.Id))
                  .Append(", \"name\": ").Append(JsonEscape(r.DisplayName))
                  .Append(", \"type\": ").Append(JsonEscape(r.TypeName))
                  .Append(", \"value\": ").Append(JsonEscape(FormatValue(r.Value)))
                  .Append(" }");
            }
            sb.Append("\n  ]");
            return sb.ToString();
        }

        // 值 → 字符串:基本类型直接 ToString,集合给个 "Count=N" 摘要避免 dump 出几兆数据,
        // 其它类型走 ToString。Inspector 是给人看的,完整保真不是目标。
        private static string FormatValue(object? v)
        {
            if (v == null) return "null";
            if (v is string s) return s;
            if (v is System.Collections.ICollection col) return $"<{v.GetType().Name} Count={col.Count}>";
            return v.ToString() ?? "null";
        }

        private static string JsonEscape(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
#endif

        private void MergeDynamicBlood(List<TopoNode> dynNodes, List<TopoLink> dynLinks)
        {
            foreach (var dn in dynNodes)
                if (!_modelNodes.Any(n => n.Id == dn.Id)) _modelNodes.Add(dn);

            long sampleNow = Stopwatch.GetTimestamp();
            foreach (var dLink in dynLinks)
            {
                var existing = _modelLinks.FirstOrDefault(l => l.Src == dLink.Src && l.Tgt == dLink.Tgt);
                if (existing == null)
                {
                    existing = new TopoLink { Src = dLink.Src, Tgt = dLink.Tgt, ToLayer = dLink.ToLayer, LastHitCount = dLink.HitCount, RateWindowStart = sampleNow };
                    _modelLinks.Add(existing);
                }
                // 流量按 RateWindowSec 窗口统计一次再平滑。以前逐帧按增量涨落热度,数据一批批到(~150ms 一批),
                // 线宽在两批之间一胖一瘦,看着一直在闪。
                int delta = Math.Max(0, dLink.HitCount - existing.LastHitCount);
                existing.LastHitCount = dLink.HitCount;
                existing.RateWindowHits += delta;
                if (existing.RateWindowStart == 0) existing.RateWindowStart = sampleNow;
                double win = (sampleNow - existing.RateWindowStart) / (double)Stopwatch.Frequency;
                if (win >= RateWindowSec)
                {
                    double rate = existing.RateWindowHits / win;
                    existing.RecentRate = existing.RecentRate * 0.5 + rate * 0.5;
                    if (existing.RecentRate < 0.2) existing.RecentRate = 0;
                    existing.RateWindowHits = 0;
                    existing.RateWindowStart = sampleNow;
                    existing.CurrentHeat = HeatOf(existing.RecentRate);
                    UpdateWidthStep(existing);
                }
            }

            foreach (var link in _modelLinks)
            {
                // 没有流量就不推进:静止的实线 = 没数据;有粒子在走 = 正在流,速度对应流量。
                // 以前有每帧 0.002 的保底速度,选中链路上不传数据的边也在慢慢流,跟"流得慢"分不清。
                if (link.CurrentHeat <= 0) continue;
                link.ParticleOffset += 0.002 + (link.CurrentHeat * 0.018);
                if (link.ParticleOffset > 1.0) link.ParticleOffset -= 1.0;
            }
        }

        private const double RateWindowSec = 0.5;

        // 线宽档位带迟滞:偏离当前档超过 3/4 档才换,流速在两档分界附近抖动时线宽不跟着跳
        private static void UpdateWidthStep(TopoLink link)
        {
            double target = link.CurrentHeat * 3;
            if (Math.Abs(target - link.WidthStep) > 0.75) link.WidthStep = (int)Math.Round(target);
        }

        // 流速 → 0..1:对数刻度,1/s 很细、100/s 封顶
        private static double HeatOf(double ratePerSec) => Math.Min(1.0, Math.Log10(1 + ratePerSec) / 2);

        private int ColumnCount => _flowColumnCount + (_hasUnlinkedColumn ? 1 : 0);

        // 视图的节点 / 连线 / 折叠开关 / 画布尺寸任一变化就重新布局;稳态每帧零开销。
        private void EnsureColumns()
        {
            double availTop = HeaderHeight + SummaryLineH;
            double availH = ActualHeight - availTop - TimelineHeight - LegendHeight;
            if (availH < 60) availH = ActualHeight; // 极小高度兜底:让出一切给节点
            int sig = HashCode.Combine(_nodes.Count, _links.Count, _foldPorts, (int)ActualWidth, (int)availH);
            if (sig == _viewSignature) return;
            _viewSignature = sig;

            var nodes = new List<LayeredGraphLayout.Node>(_nodes.Count);
            foreach (var n in _nodes)
            {
                var k = KindOf(n);
                nodes.Add(new LayeredGraphLayout.Node
                {
                    Id = n.Id,
                    Height = k == NodeKind.Port || k == NodeKind.Layer ? PortNodeH : FeatureNodeH,
                    PinFirstLayer = k == NodeKind.Source, // 数据源 / 环境 / 跨图镜像固定在最左
                });
            }
            _layout = LayeredGraphLayout.Compute(nodes, _links.Select(l => (l.Src, l.Tgt)), new LayeredGraphLayout.Options
            {
                Top = availTop,
                Width = Math.Max(1, ActualWidth),
                Height = Math.Max(1, availH),
            });

            _columnOf.Clear();
            foreach (var kv in _layout.LayerOf) _columnOf[kv.Key] = kv.Value;
            _flowColumnCount = _layout.FlowLayerCount;
            _hasUnlinkedColumn = _layout.HasUnlinkedLayer;
            _backEdges.Clear();
            foreach (var e in _layout.ReversedEdges) _backEdges.Add(e);
            _fwdSucc.Clear();
            foreach (var kv in _layout.Succ) _fwdSucc[kv.Key] = kv.Value;
            _fwdPred.Clear();
            foreach (var kv in _layout.Pred) _fwdPred[kv.Key] = kv.Value;
        }

        // 从模型生成本帧视图。折叠时:端口既有写入方又有读取方,就把它摘掉,
        // 每对 (写入方, 读取方) 连一条边,边上记下经过的端口;只有一侧的端口(没人写 / 没人读)照常显示。
        private void RebuildView()
        {
            _nodes.Clear();
            _links.Clear();
            if (!_foldPorts)
            {
                _nodes.AddRange(_modelNodes);
                _links.AddRange(_modelLinks);
                return;
            }

            var byId = new Dictionary<string, TopoNode>(_modelNodes.Count);
            foreach (var n in _modelNodes) byId[n.Id] = n;
            bool IsPort(string id) => byId.TryGetValue(id, out var n) && KindOf(n) == NodeKind.Port;

            var writers = new Dictionary<string, List<TopoLink>>();
            var readers = new Dictionary<string, List<TopoLink>>();
            foreach (var l in _modelLinks)
            {
                if (IsPort(l.Tgt) && !IsPort(l.Src))
                {
                    if (!writers.TryGetValue(l.Tgt, out var w)) writers[l.Tgt] = w = new List<TopoLink>();
                    w.Add(l);
                }
                else if (IsPort(l.Src) && !IsPort(l.Tgt))
                {
                    if (!readers.TryGetValue(l.Src, out var r)) readers[l.Src] = r = new List<TopoLink>();
                    r.Add(l);
                }
            }

            var hidden = new HashSet<string>();
            foreach (var kv in writers)
                if (readers.ContainsKey(kv.Key)) hidden.Add(kv.Key);

            foreach (var n in _modelNodes)
                if (!hidden.Contains(n.Id)) _nodes.Add(n);
            foreach (var l in _modelLinks)
                if (!hidden.Contains(l.Src) && !hidden.Contains(l.Tgt)) _links.Add(l);

            // 折叠边的状态每帧从组成它的原始连线重新汇总;粒子偏移跨帧保留在缓存对象上
            foreach (var f in _foldedLinkCache.Values)
            {
                f.Via.Clear();
                f.HitCount = 0;
                f.LastHitCount = 0;
                f.RecentRate = 0;
                f.CurrentHeat = 0;
            }
            var used = new HashSet<(string, string)>();
            foreach (var port in hidden)
            {
                foreach (var w in writers[port])
                foreach (var r in readers[port])
                {
                    if (w.Src == r.Tgt) continue; // 自己写自己读(如 ViewportManager 读写同一视口端口),折叠后是自环,不画
                    var key = (w.Src, r.Tgt);
                    if (!_foldedLinkCache.TryGetValue(key, out var f))
                        _foldedLinkCache[key] = f = new TopoLink { Src = w.Src, Tgt = r.Tgt };
                    if (!f.Via.Contains(port)) f.Via.Add(port);
                    f.LastHitCount += r.LastHitCount;
                    f.HitCount = f.LastHitCount;
                    f.RecentRate += r.RecentRate;
                    if (used.Add(key)) _links.Add(f);
                }
            }
            foreach (var key in used)
            {
                var f = _foldedLinkCache[key];
                f.CurrentHeat = HeatOf(f.RecentRate);
                UpdateWidthStep(f);
                if (f.CurrentHeat > 0) // 同上:没流量不推进
                {
                    f.ParticleOffset += 0.002 + f.CurrentHeat * 0.018;
                    if (f.ParticleOffset > 1.0) f.ParticleOffset -= 1.0;
                }
            }
        }

        // ==========================================
        // 💥 交互核心:点击选中 / 拖拽 pin / 右键 unpin
        // 拖拽阈值: 鼠标移动 > 3 px 才算 drag,否则按 click 处理(选中切换)。
        // ==========================================
        private TopoNode? _dragNode;
        private Point _dragStartPos;
        private Point _dragNodeStartTopLeft;
        private bool _draggedPastThreshold;
        private const double DragThreshold = 3.0;

        // 检测点击是否落在时间线 panel 区域(用于切换 drag-pan vs drag-node)
        private bool IsInTimelineArea(Point pos)
        {
            double timelineTop    = ActualHeight - TimelineHeight - LegendHeight;
            double timelineBottom = ActualHeight - LegendHeight;
            return pos.Y >= timelineTop && pos.Y < timelineBottom;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);

            // 时间线区:双击复位到跟随模式,单击+拖拽 = 平移历史视窗
            if (IsInTimelineArea(pos))
            {
                if (e.ClickCount >= 2)
                {
                    _timelineViewOffsetTicks = 0;
                    e.Handled = true;
                    base.OnMouseLeftButtonDown(e);
                    return;
                }
                _timelineDragActive = true;
                _timelineDragStart = pos;
                CaptureMouse();
                base.OnMouseLeftButtonDown(e);
                return;
            }

            var node = GraphViewport.Contains(pos) ? _nodes.FirstOrDefault(n => n.Bounds.Contains(ToContent(pos))) : null;

            _dragNode = node;
            _dragStartPos = pos;
            _draggedPastThreshold = false;
            if (node != null)
            {
                _dragNodeStartTopLeft = new Point(node.Bounds.X, node.Bounds.Y);
                CaptureMouse();
            }
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // 时间线拖拽:用 px → ticks 反推 offset
            if (_timelineDragActive && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(this);
                double dx = pos.X - _timelineDragStart.X;
                _timelineDragStart = pos;

#if DEBUG
                if (_tracer != null)
                {
                    long elapsed = _tracer.ElapsedTicks;
                    long viewSpan = 60 * Stopwatch.Frequency;  // 跟 DrawTimeline 同款固定 60s 窗
                    long maxOffset = Math.Max(0, elapsed - viewSpan);
                    if (maxOffset > 0)  // 数据够长才能 pan,否则 maxOffset=0 直接 clamp 死 0
                    {
                        double xWidth = Math.Max(50, ActualWidth - 8 - TimelineLabelLaneW);
                        long deltaTicks = (long)(dx / xWidth * viewSpan);
                        // grab-and-pan 语义:拖右 = 内容跟着鼠标往右走 = 视窗左移 = 看更老的
                        _timelineViewOffsetTicks = Math.Clamp(
                            _timelineViewOffsetTicks + deltaTicks, 0, maxOffset);
                    }
                }
#endif
                base.OnMouseMove(e);
                return;
            }

            if (_dragNode != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(this);
                double dx = pos.X - _dragStartPos.X;
                double dy = pos.Y - _dragStartPos.Y;

                if (!_draggedPastThreshold && (Math.Abs(dx) + Math.Abs(dy)) > DragThreshold)
                {
                    _draggedPastThreshold = true;
                    _dragNode.IsPinned = true; // 跨过阈值即被认定为"用户主动调位",pin 住
                }

                if (_draggedPastThreshold)
                {
                    var newX = _dragNodeStartTopLeft.X + dx;
                    var newY = _dragNodeStartTopLeft.Y + dy;
                    // 保持在视口内,不让节点跑到画布外面去
                    // 内容坐标:限制在整张图的内容范围内(可能比视口大)
                    var vp = GraphViewport;
                    double contentW = Math.Max(vp.Width, _layout?.ContentWidth ?? 0);
                    double contentH = Math.Max(vp.Height, _layout?.ContentHeight ?? 0);
                    newX = Math.Clamp(newX, 0, Math.Max(0, contentW - _dragNode.Bounds.Width));
                    newY = Math.Clamp(newY, vp.Top, Math.Max(vp.Top, vp.Top + contentH - _dragNode.Bounds.Height));
                    _dragNode.Bounds = new Rect(newX, newY, _dragNode.Bounds.Width, _dragNode.Bounds.Height);
                }
            }
            var hoverPos = e.GetPosition(this);
            _hoverId = GraphViewport.Contains(hoverPos) ? _nodes.FirstOrDefault(n => n.Bounds.Contains(ToContent(hoverPos)))?.Id : null;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            if (_timelineDragActive)
            {
                _timelineDragActive = false;
                base.OnMouseLeftButtonUp(e);
                return;
            }

            if (_dragNode != null && !_draggedPastThreshold)
            {
                // 阈值内未越过 → 视为单击:切换选中(同时退出触发流程回放)
                ClearFlow();
                _selectedId = _selectedId == _dragNode.Id ? null : _dragNode.Id;
            }
            else if (_dragNode == null && !_draggedPastThreshold)
            {
                // 空白处单击:清空选中
                _selectedId = null;
            }
            // 越过阈值的拖拽:位置已在 MouseMove 实时更新,这里不再做事

            _dragNode = null;
            _draggedPastThreshold = false;
            base.OnMouseLeftButtonUp(e);
        }


        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hoverId = null;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
            var node = GraphViewport.Contains(pos) ? _nodes.FirstOrDefault(n => n.Bounds.Contains(ToContent(pos))) : null;
            if (node != null)
            {
                // 右键单个节点:解除该节点的 pin,下一帧 LayoutNodes 把它收回自动布局位置
                node.IsPinned = false;
            }
            else
            {
                // 右键空白:批量解除所有 pin
                foreach (var n in _nodes) n.IsPinned = false;
            }
            e.Handled = true;
            base.OnMouseRightButtonDown(e);
        }

        // 顶部留出给 Tier 表头,底部叠加 时间线 + 颜色图例。
        // 整体布局自上而下:Header(表头) → 拓扑画布 → Timeline(时间线) → Legend(图例)。
        // LayoutNodes 内的节点垂直分布按 ActualHeight - HeaderHeight - TimelineHeight - LegendHeight 收缩。
        private const double HeaderHeight   = 28.0;
        private const double SummaryLineH   = 18.0; // 表头下一行操作提示 / 选中摘要
        private const double LegendHeight   = 44.0;
        private const double TimelineCollapsedH = 22.0;

        // 时间线默认展开 280px;窗口矮(拓扑区不到一半)时默认折叠,先让人看懂流向。T 键切换,切过一次就按用户的来。
        private bool? _timelineCollapsedByUser;
        private bool TimelineCollapsed => _timelineCollapsedByUser ?? ActualHeight < 800;
        private double TimelineHeight => TimelineCollapsed ? TimelineCollapsedH : 280.0;

        private string? _hoverId;

        // 图放不下时不压缩(压缩会让节点叠在一起),改成滚动:滚轮纵向、Shift+滚轮横向。
        private double _scrollX, _scrollY;

        private Rect GraphViewport => new Rect(0, HeaderHeight + SummaryLineH, Math.Max(0, ActualWidth),
            Math.Max(0, ActualHeight - HeaderHeight - SummaryLineH - TimelineHeight - LegendHeight));

        private Point ToContent(Point p) => new Point(p.X + _scrollX, p.Y + _scrollY);

        private double MaxScrollX => Math.Max(0, (_layout?.ContentWidth ?? 0) - GraphViewport.Width);
        private double MaxScrollY => Math.Max(0, (_layout?.ContentHeight ?? 0) - GraphViewport.Height);

        private void ClampScroll()
        {
            _scrollX = Math.Max(0, Math.Min(_scrollX, MaxScrollX));
            _scrollY = Math.Max(0, Math.Min(_scrollY, MaxScrollY));
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (GraphViewport.Contains(e.GetPosition(this)) && (MaxScrollX > 0 || MaxScrollY > 0))
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 || MaxScrollY <= 0) _scrollX -= e.Delta;
                else _scrollY -= e.Delta;
                ClampScroll();
                e.Handled = true;
            }
            base.OnMouseWheel(e);
        }

        // 内容超出时在视口右边 / 下边画细滚动条,提示还有内容
        private void DrawScrollIndicators(DrawingContext dc)
        {
            var vp = GraphViewport;
            var thumb = ScrollThumbBrush;
            if (MaxScrollY > 0 && _layout != null)
            {
                double h = vp.Height * vp.Height / _layout.ContentHeight;
                double y = vp.Top + (vp.Height - h) * (_scrollY / MaxScrollY);
                dc.DrawRoundedRectangle(thumb, null, new Rect(vp.Right - 6, y, 4, h), 2, 2);
            }
            if (MaxScrollX > 0 && _layout != null)
            {
                double w = vp.Width * vp.Width / _layout.ContentWidth;
                double x = vp.Left + (vp.Width - w) * (_scrollX / MaxScrollX);
                dc.DrawRoundedRectangle(thumb, null, new Rect(x, vp.Bottom - 6, w, 4), 2, 2);
            }
        }

        // 时间线交互状态:可拖拽平移(_timelineDragActive)。横轴域 = [0, max(now, 桶最老 ticks)],
        // 用户拖时把视窗左右滑动 _timelineViewOffsetTicks。
        private bool _timelineDragActive;
        private Point _timelineDragStart;
        private long _timelineViewOffsetTicks; // 0 = 跟随 now;>0 = 向左滚显历史

        // 当前 Render 帧用的 friendly name 表 —— DrawTimeline + 拓扑画布共享,保持两边对齐。
        private Dictionary<string, string> _frameNodeDisplay = new();

        private void Render()
        {
            if (ActualWidth < 10) return;

            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(_bgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

            // 帧首先算 friendly 名表,后续 topology 画布 + timeline 都从这里查。
#if DEBUG
            if (_tracer != null)
            {
                var featureNamesNow = BuildFeatureNameMap();
                var portWritersNow = BuildPortWriterMap(featureNamesNow);
                var portDisplayNow = BuildPortDisplayMap(portWritersNow, featureNamesNow);
                _frameNodeDisplay = new Dictionary<string, string>(featureNamesNow.Count + portDisplayNow.Count);
                foreach (var kv in featureNamesNow) _frameNodeDisplay[kv.Key] = kv.Value;
                foreach (var kv in portDisplayNow) _frameNodeDisplay[kv.Key] = kv.Value;
            }
#endif

            RebuildView();

            // 空态:还没扫到任何节点 / 任何连线 → 给 dev 一个明确的"在等数据"信号,
            // 避免误以为是渲染坏了。
            if (_nodes.Count == 0)
            {
                var emptyFt = new FormattedText(
                    "等待数据流入… (尚未捕获 Feature / 引脚)",
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    _font, 13, Brushes.DimGray, 1.0);
                dc.DrawText(emptyFt, new Point((ActualWidth - emptyFt.Width) / 2, (ActualHeight - emptyFt.Height) / 2));
                return;
            }

            LayoutNodes();
            DrawTierHeaders(dc);
            ComputeLineage();

            // 帧时间戳:节点脉冲根据它跟 _tracer.LastHitTicks 的差值算淡入淡出。
            long now = Stopwatch.GetTimestamp();

            // ==========================================
            // 💥 高级算法：智能引脚梳理 (Port Spreading)
            // 根据连接目标的高低，自动把线条均匀分布在节点的右侧和左侧边缘
            // ==========================================
            BuildNodeIndex();
            BuildPortGroups();

            UpdateFlowReveal();
            ClampScroll();
            dc.PushClip(new RectangleGeometry(GraphViewport));
            dc.PushTransform(new TranslateTransform(-_scrollX, -_scrollY));

            // 绘制管线
            foreach (var link in _links)
            {
                if (!_nodeById.TryGetValue(link.Src, out var src) || !_nodeById.TryGetValue(link.Tgt, out var tgt)) continue;

                // 焦点状态:选中节点后,整条上游链(数据从哪来)和下游链(数据到哪去)都亮,其余压暗。
                // 只亮直接相邻的话,看不出"这根端口最终喂给了哪些图层"。
                bool isUpstream = _selectedId != null && _upstream.Contains(link.Src) && _upstream.Contains(link.Tgt);
                bool isDownstream = _selectedId != null && _downstream.Contains(link.Src) && _downstream.Contains(link.Tgt);
                bool isTargeted = isUpstream || isDownstream;
                bool isDirect = _selectedId != null && (link.Src == _selectedId || link.Tgt == _selectedId);
                bool isDimmed = _selectedId != null && !isTargeted; // 没被选中的统统变暗
                bool isBack = _backEdges.Contains((link.Src, link.Tgt));
                bool inFlow = false;
                if (_flow != null)
                {
                    inFlow = FlowEdgeRevealed(link);
                    isTargeted = inFlow;
                    isDimmed = !inFlow;
                    isDirect = false;
                }

                // 💥 动态计算出口与入口的垂直坐标
                var outLinks = _srcGroups[link.Src];
                var inLinks = _tgtGroups[link.Tgt];

                int outIdx = outLinks.IndexOf(link);
                int inIdx = inLinks.IndexOf(link);

                // 让出入口在节点的边缘均匀散开
                double outY = src.Bounds.Top + (src.Bounds.Height / (outLinks.Count + 1)) * (outIdx + 1);
                double inY = tgt.Bounds.Top + (tgt.Bounds.Height / (inLinks.Count + 1)) * (inIdx + 1);

                Point p1 = new Point(src.Bounds.Right, outY);
                Point p4 = new Point(tgt.Bounds.Left, inY);

                // 💥 优化贝塞尔曲线：更有张力的S型曲线 (Sankey Style)
                double dx = Math.Abs(p4.X - p1.X);
                double dy = Math.Abs(p4.Y - p1.Y);
                // 增加 Y 轴补偿，使得跨度很大的线条弧度更加饱满平滑
                double cpX = Math.Max(50, dx * 0.45 + dy * 0.15);

                var geo = new StreamGeometry();
                Point arrowFrom;
                var poly = RouteFor(link, src, tgt);
                if (poly != null)
                {
                    using (var ctx = geo.Open()) AppendRoundedPolyline(ctx, poly, 6);
                    p1 = poly[0];
                    p4 = poly[poly.Count - 1];
                    arrowFrom = poly[poly.Count - 2];
                }
                else if (isBack)
                {
                    // 回写边(环路里被摘掉的那条)从两端节点底部往下绕回去,不跟正向连线搅在一起。
                    p1 = new Point(src.Bounds.Left + src.Bounds.Width / 2, src.Bounds.Bottom);
                    p4 = new Point(tgt.Bounds.Left + tgt.Bounds.Width / 2, tgt.Bounds.Bottom);
                    double sag = 30 + Math.Abs(p4.X - p1.X) * 0.12;
                    double lowY = Math.Max(p1.Y, p4.Y) + sag;
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(p1, false, false);
                        ctx.BezierTo(new Point(p1.X, lowY), new Point(p4.X, lowY), p4, true, false);
                    }
                    arrowFrom = new Point(p4.X, lowY);
                }
                else
                {
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(p1, false, false);
                        ctx.BezierTo(new Point(p1.X + cpX, p1.Y), new Point(p4.X - cpX, p4.Y), p4, true, false);
                    }
                    arrowFrom = new Point(p4.X - cpX, p4.Y);
                }

                // 颜色只表示"这是什么边"(数据 / 推给图层 / 回写 / 选中后的上下游),流量大小只看线宽和粒子。
                // 以前热度也用颜色(青 / 红 / 蓝),跟回写琥珀、上游金混在一起,高频红线常被看成环路。
                double heat = link.CurrentHeat;
                double thickness = 1.0 + link.WidthStep * 0.5; // 1 / 1.5 / 2 / 2.5 四档,不随小波动变化
                if (isTargeted) thickness = Math.Max(thickness, 2.0);
                Brush pipeColor = inFlow ? FlowBrush
                    : isUpstream ? UpstreamBrush
                    : isDownstream ? DownstreamBrush
                    : isBack ? BackEdgeBrush
                    : link.ToLayer ? LayerLinkBrush
                    : DataLinkBrush;

                // 压暗用画刷自身的透明度,不开 PushOpacity 层:每个透明层都要渲染线程单独合成一张中间图,
                // 选中后几十条暗线同时压暗,渲染线程吃满一个核,图表跟着掉帧。
                // 暗线不画粒子和标签,只有线和箭头,两者只在箭头尖重叠几个像素。
                if (isDimmed) pipeColor = DimmedBrush(pipeColor, 0.1);
                Pen pipePen = CachedPen(pipeColor, thickness, isBack);

                // 读 vs 写:src 是 port (tier 1) 时是"port → feature"语义,即被读取;否则是写入。
                // 读连线给空心箭头,写连线给实心箭头,一眼分清方向。
                bool isRead = src.Tier == 1;

                dc.DrawGeometry(null, pipePen, geo);

                // 端点箭头:Bezier 在 t=1 处的切线方向 ≈ p4 - cp2 (cp2 = p4.X - cpX, p4.Y)
                DrawArrow(dc, p4, arrowFrom, pipeColor, isTargeted ? 8 : 6, hollow: isRead);

                // 如果处于暗淡状态，就不画粒子了，避免视觉杂乱
                // 粒子只在选中的上下游链上跑:全图都跑的时候满屏白点,反而看不出哪条在动
                // 粒子只画在真有数据流过的边上:选中模式看实时流量(流量为 0 的边保持静止实线);
                // 触发流程回放里亮起的边是捕获到的那一波,一定流过数据,粒子用回放时钟推进。
                bool showParticles = !isBack && (inFlow || (isTargeted && link.CurrentHeat > 0));
                if (showParticles)
                {
                    double offset = inFlow ? FlowParticlePhase() : link.ParticleOffset;
                    if (poly != null) DrawFlowParticleAlong(dc, poly, offset, Math.Max(heat, 0.3), isTargeted);
                    else DrawFlowParticle(dc, p1, p4, cpX, offset, Math.Max(heat, 0.3), isTargeted);
                }

                // 选中节点关联的连线在中点显示 命中速率 + 累计数。
                // 速率比单纯总数更直观地说明"这条链路现在还在不在动"。
                if (isDirect && !isBack && link.LastHitCount > 0)
                {
                    if (poly != null) DrawLinkHitCountAt(dc, PointAlong(poly, 0.5), link, pipeColor);
                    else DrawLinkHitCount(dc, p1, p4, cpX, link, pipeColor);
                }
            }

            // 绘制节点。有选中 / 回放时,不在链上的节点压暗。
            // 暗节点合进同一个透明层先画:节点互不重叠,整层 0.2 跟逐个 0.2 画出来一样,
            // 渲染线程却只合成一张中间图(逐个开层时几十张,选中后渲染线程吃满一个核)。
            bool anyDimmed = false;
            foreach (var node in _nodes)
                if (IsNodeDimmed(node)) { anyDimmed = true; break; }
            if (anyDimmed)
            {
                dc.PushOpacity(0.2);
                foreach (var node in _nodes)
                    if (IsNodeDimmed(node)) DrawNode(dc, node, now, isDimmed: true);
                dc.Pop();
            }
            foreach (var node in _nodes)
                if (!IsNodeDimmed(node)) DrawNode(dc, node, now, isDimmed: false);

            DrawNodeCallout(dc);
            dc.Pop(); // transform
            dc.Pop(); // clip
            DrawScrollIndicators(dc);
            DrawSelectionSummary(dc);
            DrawFlowPanel(dc);

            // 关键事件时间线 —— compose 起止 / 单 feature 接入 / 端口首次写 / handler 缺失 等永久层事件,
            // 给"右键打开 inspector 是滞后行为,想看从启动到现在发生过啥"这个用户场景。
            DrawTimeline(dc);

            // 颜色图例(右下角小卡片) — 让 heat 颜色含义可被新人一眼读懂。
            DrawLegend(dc);
        }

        private bool IsNodeDimmed(TopoNode node) => _flow != null
            ? !_flowRevealedNodes.Contains(node.Id)
            : _selectedId != null && !_upstream.Contains(node.Id) && !_downstream.Contains(node.Id);

        private void DrawNode(DrawingContext dc, TopoNode node, long now, bool isDimmed)
        {
            bool isSelected = node.Id == _selectedId;
            var kind = KindOf(node);

            var r = node.Bounds;
            bool isPort = kind == NodeKind.Port;
            Brush fill = isSelected ? CardSelectedBrush : isPort ? PortFillBrush : _nodeBrush;
            Brush border = isSelected ? SelectedBorderBrush : isPort ? PortBorderBrush : CardBorderBrush;
            double corner = isPort ? r.Height / 2 : 6;
            // 静默:收到过数据但超过 IdleAfterSec 没再收到 → 虚线框 + 文字变暗。
            // 数据正常流动时图上什么都不标,哪里断流才显出来(以前"刚收到数据"的绿点在持续流动时一直亮,等于没说)。
            bool idle = !isSelected && IsIdle(node, now);
            var borderPen = CachedPen(idle ? TextMutedBrush : border, isSelected ? 1.5 : 1.0, idle);
            dc.DrawRoundedRectangle(fill, borderPen, r, corner, corner);

            // 活跃:1 秒内收到过数据,右上角一个固定的绿点(不随每批数据闪);3 秒没数据的走上面的静默虚线框
            if (!idle && !isDimmed && kind != NodeKind.Layer
                && SecondsSinceLastHit(node.Id, now) is double sinceHit && sinceHit < ActiveWithinSec)
                dc.DrawEllipse(ActiveDotBrush, null, new Point(r.Right - 7, r.Top + 7), 2.5, 2.5);

            // 触发流程:已回放到的节点左上角标被触发的先后序号
            if (_flow != null && _flowRevealedNodes.Contains(node.Id) && _flowNodeOrder.TryGetValue(node.Id, out int order))
            {
                var num = Text(order.ToString(CultureInfo.InvariantCulture), _monoFont, 10, FlowNumberBrush);
                double rad = Math.Max(8, num.Width / 2 + 4);
                var c = new Point(r.Left + 2, r.Top + 2);
                dc.DrawEllipse(FlowBrush, null, c, rad, 8);
                DrawText(dc, num, new Point(c.X - num.Width / 2, c.Y - num.Height / 2));
            }

            // 卡片左侧细色条标类别(数据源绿 / 图层紫 / Feature 灰蓝),端口不加,保持低调
            if (!isPort)
            {
                Brush accent = kind == NodeKind.Source ? SourceAccentBrush
                    : kind == NodeKind.Layer ? LayerBorderBrush
                    : FeatureAccentBrush;
                dc.DrawRoundedRectangle(accent, null, new Rect(r.X + 1.5, r.Y + 5, 3, Math.Max(2, r.Height - 10)), 1.5, 1.5);
            }


            // Pin 指示:被用户拖拽过的节点左上角画琥珀小点,提示这个位置是手动定的、不会被自动布局重置
            if (node.IsPinned)
            {
                dc.DrawEllipse(PinDotBrush, null, new Point(node.Bounds.Left + 5, node.Bounds.Top + 5), 2.5, 2.5);
            }

            // 优先用帧 friendly 名表(同名 feature 加 hash / port 走 writer 前缀 + 重名消歧),
            // 跟下面时间线 panel 的 port 名严格对齐。表里没有就回退原 Label。
            string displayLabel = DisplayLabelOf(node);
            Brush textBrush = isSelected ? Brushes.White : idle ? TextMutedBrush : isPort ? TextSecondaryBrush : TextPrimaryBrush;
            double textLeft = isPort ? 10 : 12;
            // 节点窄时截成省略号,不再压到相邻列;全名在悬停 / 选中时显示
            var ft = Text(displayLabel, isPort ? _font : _fontStrong, isPort ? 11 : 12, textBrush,
                maxWidth: Math.Max(10, r.Width - textLeft - 12));
            DrawText(dc, ft, new Point(r.Left + textLeft, r.Top + (r.Height - ft.Height) / 2));

#if DEBUG
            // Per-feature 性能徽章:tier 2 + 有采样数据时,右上角小字显示平均 OnProject 耗时。
            // 颜色阈值: <0.3ms 海绿(健康) / <1ms 黄色(关注) / >=1ms 橘红(瓶颈嫌疑)
            // 💥 TopologyTracer.FeatureCost 仅在 DEBUG 编译条件下声明,Release 不存在该字段 ——
            //    整段徽章逻辑包在 #if DEBUG 里跟字段定义对齐,否则 Release 编译挂掉。
            // 只标值得关注的(≥0.1ms);每个节点都挂 "<.05ms" 只会添乱,全量数字在悬停气泡里看
            if (node.Tier == 2 && _tracer != null
                && _tracer.FeatureCost.TryGetValue(node.Id, out var cost) && cost.Samples > 0
                && (cost.TotalTicks / (double)cost.Samples) / Stopwatch.Frequency * 1000.0 >= 0.1)
            {
                double avgMs = (cost.TotalTicks / (double)cost.Samples) / Stopwatch.Frequency * 1000.0;
                string badge = avgMs.ToString("F2");
                Brush badgeColor = avgMs >= 1.0 ? BadgeHotBrush
                                   : avgMs >= 0.3 ? UpstreamBrush
                                   : TextMutedBrush;
                var badgeFt = Text(badge + "ms", _monoFont, 9, badgeColor);
                // 放在节点框外右下,不跟名字抢位置
                DrawText(dc, badgeFt, new Point(node.Bounds.Right - badgeFt.Width - 2, node.Bounds.Bottom + 1));
            }
#endif
        }

        // ──── 时间线 panel(横轴 = 时间,纵向分:key events 行 + per-port 活动柱)──────────
        // 自上而下分 4 个区:
        //   1. 标题栏(高度 22) —— "⏱ 时间线" + 当前时长 + 视窗范围
        //   2. 时间轴刻度(高度 18) —— 主刻度 + label
        //   3. Key events 行(高度 18) —— 6 类事件不同颜色的圆点
        //   4. Per-port 活动柱区(剩余) —— 每根 port 一行,横向铺 240 桶活动强度
        // 横轴域 [vMin, vMax] 默认 [0, ElapsedTicks],拖拽 _timelineViewOffsetTicks 可往老滑;
        // 鼠标滚轮 zoom 暂未实装,等用户提了再加。
        private const double TimelineLabelLaneW = 220.0; // 左侧 port 名称列宽(留够 "MarketScanner › ChangePercent" 这种 writer-prefix 长名)
        private const double TimelineHeaderH    = 22.0;
        private const double TimelineAxisH      = 18.0;
        private const double TimelineKeyEventsH = 18.0;
        private const double TimelinePortLaneH  = 18.0;  // 行高从 14 → 18,字立得起来 + zebra 视觉分得开

        private void DrawTimeline(DrawingContext dc)
        {
#if DEBUG
            if (_tracer == null) return;

            double top    = ActualHeight - TimelineHeight - LegendHeight;
            double bottom = ActualHeight - LegendHeight;
            var area = new Rect(0, top, ActualWidth, TimelineHeight);

            if (TimelineCollapsed)
            {
                dc.DrawRectangle(TimelineBgBrush, null, area);
                var hint = Text(
                    $"⏱ 时间线(已折叠,按 T 展开) · 已运行 {_tracer.ElapsedTicks / (double)Stopwatch.Frequency:F1}s · 关键事件 {_tracer.KeyEvents.Count} 条",
                    _font, 11, Brushes.SlateGray);
                DrawText(dc, hint, new Point(8, top + (TimelineCollapsedH - hint.Height) / 2));
                return;
            }

            // 整块底色 + 顶部分隔
            dc.DrawRectangle(TimelineBgBrush, null, area);
            dc.DrawLine(CachedPen(TimelineSepBrush, 1), new Point(0, top), new Point(ActualWidth, top));

            long elapsed = _tracer.ElapsedTicks;
            if (elapsed <= 0) return;

            // 视窗宽度 = 固定 60s。窗口随 offset 整体平移:
            //   - elapsed <= viewSpan(早期):数据全装得下,显示 [0, elapsed],平移无意义
            //   - elapsed >  viewSpan(晚期):
            //       offset=0 跟随 now    → 视窗 [elapsed - viewSpan, elapsed]
            //       offset>0 看更老的    → 视窗 [elapsed - viewSpan - offset, elapsed - offset]
            //       offset 上限 = elapsed - viewSpan(滑到最左,T+0s 贴左)
            long viewSpan = 60 * Stopwatch.Frequency;       // 60s 默认窗口宽度,兼顾密度跟全局感
            long maxOffset = Math.Max(0, elapsed - viewSpan);
            _timelineViewOffsetTicks = Math.Clamp(_timelineViewOffsetTicks, 0, maxOffset);

            long vMax, vMin;
            if (elapsed <= viewSpan)
            {
                // 数据少,全显:窗口 [0, elapsed],平移此时无效。
                vMin = 0;
                vMax = Math.Max(elapsed, Stopwatch.Frequency); // 防 vMax-vMin=0 出现的除 0
            }
            else
            {
                vMax = elapsed - _timelineViewOffsetTicks;
                vMin = vMax - viewSpan;
            }

            double xLeft = TimelineLabelLaneW;
            double xRight = ActualWidth - 8;
            double xWidth = Math.Max(50, xRight - xLeft);

            // ── 1. 标题栏 ──────────────────────────────────────────────────────
            double secElapsed = elapsed / (double)Stopwatch.Frequency;
            double secViewMax = vMax / (double)Stopwatch.Frequency;
            double secViewMin = vMin / (double)Stopwatch.Frequency;
            string follow = _timelineViewOffsetTicks == 0 ? " · 跟随" : " · 历史回看";
            var titleFt = Text(
                $"⏱ 时间线   总时长 T+{secElapsed:F1}s   视窗 T+{secViewMin:F1}s ~ T+{secViewMax:F1}s{follow}   (左拖平移)",
                _font, 12, TimelineTitleBrush);
            DrawText(dc, titleFt, new Point(8, top + 4));

            double yAxis = top + TimelineHeaderH;
            double yKeyEvents = yAxis + TimelineAxisH;
            double yPortsTop = yKeyEvents + TimelineKeyEventsH;
            double yPortsBottom = bottom;

            // ── 2. 时间轴刻度 ──────────────────────────────────────────────────
            DrawTimelineAxis(dc, xLeft, xRight, yAxis, vMin, vMax);

            // ── 3. Key events 行 ──────────────────────────────────────────────
            DrawTimelineKeyEvents(dc, xLeft, xRight, yKeyEvents, vMin, vMax);

            // ── 4. Per-port 活动柱 ─────────────────────────────────────────────
            DrawTimelinePortLanes(dc, xLeft, xRight, yPortsTop, yPortsBottom, vMin, vMax);
#endif
        }

        private void DrawTimelineAxis(DrawingContext dc, double xLeft, double xRight, double yTop,
                                      long vMin, long vMax)
        {
            double laneW = xRight - xLeft;
            double rangeSec = (vMax - vMin) / (double)Stopwatch.Frequency;
            // 选个差不多的主刻度间隔(目标 6~10 个刻度)
            double step = PickAxisStep(rangeSec / 8);
            double stepTicks = step * Stopwatch.Frequency;
            var tickPen = CachedPen(TimelineTickBrush, 1);

            // 底线
            dc.DrawLine(tickPen, new Point(xLeft, yTop + TimelineAxisH - 1),
                                  new Point(xRight, yTop + TimelineAxisH - 1));

            double startSec = Math.Ceiling((vMin / (double)Stopwatch.Frequency) / step) * step;
            for (double s = startSec; s <= rangeSec + vMin / (double)Stopwatch.Frequency + 0.001; s += step)
            {
                long t = (long)(s * Stopwatch.Frequency);
                double x = xLeft + (t - vMin) / (double)(vMax - vMin) * laneW;
                if (x < xLeft || x > xRight) continue;
                dc.DrawLine(tickPen, new Point(x, yTop + 4), new Point(x, yTop + TimelineAxisH - 1));
                var lbl = Text($"T+{s:F0}s", _font, 10, TimelineAxisLabelBrush);
                DrawText(dc, lbl, new Point(x + 2, yTop + 1));
            }
        }

        // 选 1/2/5/10/30/60/120/300 这种"人能读出来"的刻度间隔。
        private static double PickAxisStep(double approxStepSec)
        {
            double[] candidates = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            foreach (var c in candidates) if (c >= approxStepSec) return c;
            return 600;
        }

#if DEBUG
        // 时间线明细绘制依赖 TopologyTracer 的 DEBUG-only 成员,调用方 DrawTimeline 也只在 DEBUG 下有内容。
        private void DrawTimelineKeyEvents(DrawingContext dc, double xLeft, double xRight, double y,
                                           long vMin, long vMax)
        {
            if (_tracer == null) return;
            var events = _tracer.DumpKeyEvents();
            double laneW = xRight - xLeft;

            // 左侧标签
            var lbl = Text("Key Events", _font, 11, TimelineKeyLabelBrush);
            DrawText(dc, lbl, new Point(8, y + 2));

            double cy = y + TimelineKeyEventsH / 2.0;
            foreach (var ev in events)
            {
                if (ev.TicksFromStart < vMin || ev.TicksFromStart > vMax) continue;
                double x = xLeft + (ev.TicksFromStart - vMin) / (double)(vMax - vMin) * laneW;
                var (_, brush) = TimelineKindStyle(ev.Kind);
                dc.DrawEllipse(brush, null, new Point(x, cy), 4, 4);
            }
        }

        // 排序 + 渲染 per-port 活动柱:
        //  - 只显示 PortActivity 里 Count > 0 的 port
        //  - 按 FirstPortWrite 时间戳升序(老的在上)
        //  - 每根 port 一行 14px,显示左侧 port 名 + 右侧的 240 桶活动柱
        //  - 柱高 = 该桶的 WriteDelta 占该 port 全程 max 的比例;颜色 = 蓝渐变(冷=低活动)
        //  - 整行最右几桶若 WriteDelta=0(超过 5 桶 = 2.5s)→ port 名显黄色("⚠ 静默"提示)
        private void DrawTimelinePortLanes(DrawingContext dc, double xLeft, double xRight,
                                           double yTop, double yBottom, long vMin, long vMax)
        {
            if (_tracer == null) return;
            double laneW = xRight - xLeft;
            double availH = yBottom - yTop;
            int maxLanes = Math.Max(1, (int)(availH / TimelinePortLaneH));

            // 收集 port → first-write timestamp(从 KeyEvents 找,没找到的退化用 0)
            var firstWrite = new Dictionary<string, long>();
            foreach (var ev in _tracer.DumpKeyEvents())
            {
                if (ev.Kind == TopologyTracer.EventKind.FirstPortWrite && !firstWrite.ContainsKey(ev.TargetId))
                    firstWrite[ev.TargetId] = ev.TicksFromStart;
            }

            var lanes = _tracer.PortActivity
                .Where(kv => kv.Value.Count > 0)
                .OrderBy(kv => firstWrite.GetValueOrDefault(kv.Key, long.MaxValue))
                .Take(maxLanes)
                .ToList();

            if (lanes.Count == 0)
            {
                var emptyFt = Text("(尚无 port 活动 —— schema compose 后等数据流过来)", _font, 11, TimelineEmptyBrush);
                DrawText(dc, emptyFt, new Point(8, yTop + 4));
                return;
            }

            long now = Stopwatch.GetTimestamp() - _tracer.StartTicks;
            long quietThresholdTicks = (long)(2.5 * Stopwatch.Frequency); // 2.5s 没新写就算静默

            // _frameNodeDisplay 在 Render() 头部已经算好(featureName + portWriter + portDisplay 三层去重管线),
            // 下面查表直接用,不重复算。

            // 行间区分配色:zebra 双色底,选中浅蓝,静默橙红 outline
            var zebraOdd  = ZebraOddBrush;
            var zebraEven = ZebraEvenBrush;
            var labelColBg = LabelColumnBrush;
            var laneSepPen = CachedPen(LaneSepBrush, 1);

            for (int i = 0; i < lanes.Count; i++)
            {
                var pid = lanes[i].Key;
                var ring = lanes[i].Value;
                double y = yTop + i * TimelinePortLaneH;

                long lastWriteTicksFromStart = ring.LastSampleTicks - _tracer.StartTicks;
                bool isQuiet = (now - lastWriteTicksFromStart) > quietThresholdTicks
                          && ring.WriteDelta[(ring.Head - 1 + TopologyTracer.ActivityRing.CAP) % TopologyTracer.ActivityRing.CAP] == 0;
                bool selected = _selectedId != null && pid == _selectedId;

                // ── 行底色:三层叠 ──
                //   1) zebra 底色(隔行不同灰度,基础视觉锚)
                //   2) 左侧 label 列单独底色,跟右侧活动柱区视觉切分
                //   3) 选中态 蓝半透明覆盖(整行)
                dc.DrawRectangle(i % 2 == 0 ? zebraEven : zebraOdd, null,
                    new Rect(0, y, ActualWidth, TimelinePortLaneH));
                dc.DrawRectangle(labelColBg, null,
                    new Rect(0, y, TimelineLabelLaneW, TimelinePortLaneH));
                if (selected)
                {
                    dc.DrawRectangle(LaneSelectedBrush, null,
                        new Rect(0, y, ActualWidth, TimelinePortLaneH));
                }

                // 行底分隔线(细 1px,半透明) —— 行多时一眼能数清
                dc.DrawLine(laneSepPen, new Point(0, y + TimelinePortLaneH - 0.5),
                                         new Point(ActualWidth, y + TimelinePortLaneH - 0.5));

                // ── 左侧 port 名 ──
                // 走 _frameNodeDisplay(已经 writer 前缀 + 重名 hash 后缀都处理过)。
                string label = _frameNodeDisplay.GetValueOrDefault(pid) ?? FriendlyPortName(pid);
                if (label.Length > 30) label = label.Substring(0, 29) + "…";
                Brush nameBrush = isQuiet
                    ? LaneQuietBrush        // 黄:静默
                    : (selected
                        ? LaneSelectedTextBrush  // 选中:浅蓝
                        : LaneTextBrush);
                string prefix = isQuiet ? "⚠ " : "  ";
                var nameFt = Text(prefix + label, _font, 11, nameBrush);
                DrawText(dc, nameFt, new Point(8, y + (TimelinePortLaneH - nameFt.Height) / 2));

                // label 列右边竖分隔
                dc.DrawLine(CachedPen(LabelColumnSepBrush, 1),
                    new Point(TimelineLabelLaneW, y),
                    new Point(TimelineLabelLaneW, y + TimelinePortLaneH));

                // 计算该 port 全程 max(用于柱高归一化)
                int maxDelta = 0;
                for (int b = 0; b < ring.Count; b++)
                {
                    if (ring.WriteDelta[b] > maxDelta) maxDelta = ring.WriteDelta[b];
                }
                if (maxDelta == 0) continue; // 全无写入,只显示静默标签,跳过画柱

                // 渲染桶。Head 指向下一个空位,真实数据是 Head-Count..Head-1。
                int start = (ring.Head - ring.Count + TopologyTracer.ActivityRing.CAP) % TopologyTracer.ActivityRing.CAP;
                for (int k = 0; k < ring.Count; k++)
                {
                    int b = (start + k) % TopologyTracer.ActivityRing.CAP;
                    long bt = ring.BucketTicks[b];
                    if (bt < vMin || bt > vMax) continue;
                    int wd = ring.WriteDelta[b];
                    if (wd == 0) continue;
                    double x = xLeft + (bt - vMin) / (double)(vMax - vMin) * laneW;
                    double h = (TimelinePortLaneH - 4) * (wd / (double)maxDelta);
                    double bw = Math.Max(2, laneW / Math.Max(40, ring.Count));
                    if (bw > 4) bw = 4;
                    var heat = wd / (double)maxDelta;
                    var bar = CachedBrush(
                        (byte)(150 + heat * 105),
                        (byte)(80 + heat * 50),
                        (byte)(140 + heat * 80),
                        (byte)(220));
                    dc.DrawRectangle(bar, null,
                        new Rect(x - bw/2, y + TimelinePortLaneH - 2 - h, bw, h));
                }
            }
        }

        // 类别 → (icon, 颜色画刷)。颜色跟 legend 风格一致:绿=正常 / 黄=警告 / 红=错误 / 蓝=数据流 / 紫=DataSource publish。
        private static (string Icon, SolidColorBrush Brush) TimelineKindStyle(TopologyTracer.EventKind k) => k switch
        {
            TopologyTracer.EventKind.ComposeStart        => ("●", CachedBrush(255, 100, 200, 255)),
            TopologyTracer.EventKind.ComposeEnd          => ("◆", CachedBrush(255, 100, 200, 255)),
            TopologyTracer.EventKind.FeatureComposed     => ("✓", CachedBrush(255, 120, 200, 120)),
            TopologyTracer.EventKind.FeatureShortCircuit => ("⚠", CachedBrush(255, 240, 190,  80)),
            TopologyTracer.EventKind.FirstPortWrite      => ("▶", CachedBrush(255, 140, 180, 230)),
            TopologyTracer.EventKind.HandlerMissing      => ("✗", CachedBrush(255, 240,  90,  90)),
            // 紫色菱形 —— DataSource Publish 是数据流入 schema 的最上游事件,跟下游 FirstPortWrite 区分开
            TopologyTracer.EventKind.DataSourcePublish   => ("◈", CachedBrush(255, 200, 140, 230)),
            _                                            => ("·", CachedBrush(255, 200, 200, 200)),
        };
#endif

        // target id 优先查 NodeMetadata 取展示名,查不到回退原 id(handler / scheme name 等没节点的 case)。
        // disambiguateBy:为通用端口名(OutputPort / InputPort 等)去重时,
        //   传一个"上游 feature 名 → port"的 prefix。null = 不加前缀,直接用 stripped 名。
        private string? ResolveDisplayName(string id, string? writerHint = null)
        {
#if DEBUG
            string baseName = (_tracer != null && _tracer.NodeMetadata.TryGetValue(id, out var meta))
                ? FriendlyPortName(meta.Name)
                : FriendlyPortName(id);

            // 通用名加 writer 前缀。WellKnown(VP_Xxx, Plot › Xxx)已经有上下文,不重复加。
            if (writerHint != null && IsAmbiguousPortName(baseName))
                return $"{writerHint} › {baseName}";
            return baseName;
#else
            return FriendlyPortName(id);
#endif
        }

        // 这些名字 strip 之后跟 feature 输出端口名重名,单独看认不出谁是谁,需要 writer 前缀消歧。
        private static bool IsAmbiguousPortName(string n) =>
            n == "OutputPort" || n == "InputPort" || n == "DataPort"
            || n == "HitStatePort"
            || n == "ValuePorts" || n == "YRangePort" || n == "RangePort"
            || n == "PointerHitPort" || n == "XAxisDataPort" || n == "BaseLinePort"
            || n == "DomainDataPort" || n == "ReferencePort" || n == "RawExtremaPort"
            || n.StartsWith("Inputs.", StringComparison.Ordinal)   // Compute / Plot 多输入子端口
            || n.StartsWith("SeriesOutputs.", StringComparison.Ordinal);  // PlotFeature 子序列输出端口

        // 反扫 LinkHits 一次,得到 portId → writer feature display name 的映射。
        // 写入者 = LinkHits 里 (Tier ≥ 0 的 src, port 自身) 的命中数最多的那个 src。
        // 多写入者时优先选 Feature(Tier 2),退而求其次 DataPipe(Tier 0)。
        // featureNameMap 已经把 feature 名做过去重(同名 → 加 hash 后缀),所以 writer 端到这里也唯一。
        private Dictionary<string, string> BuildPortWriterMap(Dictionary<string, string> featureNameMap)
        {
            var map = new Dictionary<string, string>();
#if DEBUG
            if (_tracer == null) return map;
            // (port id → 候选 writer + 命中数)
            var bestPerPort = new Dictionary<string, (string srcId, int hits, int srcTier)>();
            foreach (var kv in _tracer.LinkHits)
            {
                var (src, tgt) = kv.Key;
                if (!_tracer.NodeMetadata.TryGetValue(tgt, out var tgtMeta) || tgtMeta.Tier != 1) continue;
                int srcTier = _tracer.NodeMetadata.TryGetValue(src, out var sm) ? sm.Tier : -1;
                if (!bestPerPort.TryGetValue(tgt, out var prev)
                    || (srcTier == 2 && prev.srcTier != 2)             // Feature 优先
                    || (srcTier == prev.srcTier && kv.Value > prev.hits))
                {
                    bestPerPort[tgt] = (src, kv.Value, srcTier);
                }
            }
            foreach (var kv in bestPerPort)
            {
                if (featureNameMap.TryGetValue(kv.Value.srcId, out var displayName))
                    map[kv.Key] = displayName;
                else if (_tracer.NodeMetadata.TryGetValue(kv.Value.srcId, out var srcMeta))
                    map[kv.Key] = ShortenWriterName(srcMeta.Name);   // fallback,DataPipe 等非 feature
            }
#endif
            return map;
        }

        private static string ShortenWriterName(string name)
        {
            // "MarketScannerDataSource" → "MarketScanner"(去 DataSource 后缀;前面注册时已经 strip "Feature" 了,这里 strip "DataSource")
            if (name.EndsWith("DataSource", StringComparison.Ordinal))
                return name.Substring(0, name.Length - "DataSource".Length);
            return name;
        }

        // 第三层:port 最终 display name。
        // 规则:
        //   - 起手 FriendlyPortName(strip hash 前/后缀,VP_X → Viewport.X 等)
        //   - 通用名(OutputPort / InputPort / DataPort 等)加 "WriterFeature › OutputPort" 前缀
        //   - 没 writer 但有 owner(典型:孤儿 output port,如 PlotFeature.HitStatePort 在没 scatter
        //     children 的场景下没人写也没人读)→ 加 "Owner › PortName (孤儿)" 前缀,user 一眼看出归属
        //   - 完成 1+2+3 之后仍有重名(典型:多个 DataSource 都有 Latest 字段)→ 加 [<port id 前 4 位>] 后缀
        private Dictionary<string, string> BuildPortDisplayMap(
            Dictionary<string, string> portWriters,
            Dictionary<string, string> featureNames)
        {
            var initial = new Dictionary<string, string>();
#if DEBUG
            if (_tracer == null) return initial;
            foreach (var kv in _tracer.NodeMetadata)
            {
                if (kv.Value.Tier != 1) continue;
                string baseName = FriendlyPortName(kv.Value.Name);
                if (portWriters.TryGetValue(kv.Key, out var w) && IsAmbiguousPortName(baseName))
                {
                    baseName = $"{w} › {baseName}";
                }
                else if (!portWriters.ContainsKey(kv.Key)
                         && _tracer.PortOwner.TryGetValue(kv.Key, out var ownerId)
                         && featureNames.TryGetValue(ownerId, out var ownerName))
                {
                    // 孤儿 port:有 owner 但完全没 writer。加 "(孤)" 标记。
                    // 这条规则不限 IsAmbiguousPortName —— LocalHit / BoxZoomState 这种本来就唯一的名字
                    // 也加 owner 前缀,因为用户对它们的归属同样模糊(schema 自有 vs feature 自有 vs framework auto)。
                    baseName = $"{ownerName} › {baseName} (孤)";
                }
                initial[kv.Key] = baseName;
            }
            // 检测重名,加 hash 后缀
            var nameCounts = new Dictionary<string, int>();
            foreach (var v in initial.Values)
                nameCounts[v] = nameCounts.GetValueOrDefault(v) + 1;
            var final = new Dictionary<string, string>();
            foreach (var kv in initial)
            {
                if (nameCounts[kv.Value] > 1)
                {
                    // port id 前 4 位足够区分 —— 蓝图持久化路径下 id 是 hash 前缀,DataPort ctor 路径下是字段名
                    string idHint = kv.Key.Length >= 4 ? kv.Key[..4] : kv.Key;
                    final[kv.Key] = $"{kv.Value}[{idHint}]";
                }
                else
                {
                    final[kv.Key] = kv.Value;
                }
            }
            return final;
#else
            return initial;
#endif
        }

        // 扫 NodeMetadata 一次给所有 feature 算去重后的 display name。
        // ChartFeature 自带 InstanceId 后缀(LineSeries#3F2A),通常已唯一;
        // 但 DataSource 没 InstanceId,多实例同 type 时 NodeMetadata 里就是 N 个同名条目。
        // 检测同名 ≥ 2 的,统一加 [F_xxxx] 后 4 位 hash 消歧。
        // "Axis#c650" → "Axis";没有实例号的原样返回
        private static string StripInstanceSuffix(string name)
        {
            int i = name.LastIndexOf('#');
            return i > 0 ? name.Substring(0, i) : name;
        }

        private Dictionary<string, string> BuildFeatureNameMap()
        {
            var map = new Dictionary<string, string>();
#if DEBUG
            if (_tracer == null) return map;

            // 第一遍:按 ShortenWriterName 计数;另按去掉 "#实例号" 的类型名计数
            var nameCounts = new Dictionary<string, int>();
            var baseCounts = new Dictionary<string, int>();
            foreach (var kv in _tracer.NodeMetadata)
            {
                if (kv.Value.Tier != 2) continue;
                var n = ShortenWriterName(kv.Value.Name);
                nameCounts[n] = nameCounts.GetValueOrDefault(n) + 1;
                var b = StripInstanceSuffix(n);
                baseCounts[b] = baseCounts.GetValueOrDefault(b) + 1;
            }
            // 第二遍:类型唯一的去掉实例号(节点窄,"#c650" 这类后缀只在同类型多实例时才有用);同名 ≥ 2 的加 hash 后缀
            foreach (var kv in _tracer.NodeMetadata)
            {
                if (kv.Value.Tier != 2) continue;
                var shortened = ShortenWriterName(kv.Value.Name);
                var baseName = StripInstanceSuffix(shortened);
                if (baseCounts[baseName] == 1)
                {
                    map[kv.Key] = baseName;
                }
                else if (nameCounts[shortened] > 1)
                {
                    // F_<hash> id → 取最后 4 位
                    string idSuffix = kv.Key.Length >= 4 ? kv.Key[^4..] : kv.Key;
                    map[kv.Key] = $"{shortened}[{idSuffix}]";
                }
                else
                {
                    map[kv.Key] = shortened;
                }
            }
#endif
            return map;
        }

        // 把"机器友好"的 port id / displayName 翻成"人类友好":
        //   "91a5a79d_Latest"                                  → "Latest"
        //   "91a5a79d_Latest_3F2A1B7C"  (DataPort ctor 加 GUID 后缀)→ "Latest"
        //   "VP_LogicalLength"                                 → "Viewport.LogicalLength"
        //   "Plot.scatter_amount_ratio.scatter_amount_ratio"   → "Plot › scatter_amount_ratio"(同名只显一次)
        //   "F_xxxxx"                                          → 原样(feature 节点,NodeMetadata 通常已给 Name)
        //   "OutputPort_3F2A1B7C"                              → "OutputPort"
        private static string FriendlyPortName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            // VP_X → Viewport.X
            if (raw.StartsWith("VP_", StringComparison.Ordinal))
                return "Viewport." + raw.Substring(3);

            // Plot.A.B —— A == B 时合并 (PlotFeature 单 spec 同名场景,如 "Plot.scatter.scatter")
            if (raw.StartsWith("Plot.", StringComparison.Ordinal))
            {
                var parts = raw.Split('.');
                if (parts.Length == 3 && parts[1] == parts[2]) return $"Plot › {parts[1]}";
                if (parts.Length >= 3) return $"Plot › {parts[1]}.{string.Join('.', parts, 2, parts.Length - 2)}";
                return raw;
            }

            // 剥末尾 GUID 后缀:_XXXXXXXX(8 hex)
            int lastUnderscore = raw.LastIndexOf('_');
            if (lastUnderscore > 0 && raw.Length - lastUnderscore - 1 == 8 && IsHex(raw, lastUnderscore + 1, 8))
                raw = raw.Substring(0, lastUnderscore);

            // 剥前缀 hex_(8 hex + 下划线):蓝图持久化路径下 GraphSerializer 给端口加 nodeId hash 前缀
            if (raw.Length > 9 && raw[8] == '_' && IsHex(raw, 0, 8))
                raw = raw.Substring(9);

            return raw;
        }

        private static bool IsHex(string s, int start, int len)
        {
            for (int i = start; i < start + len; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        // ==========================================
        // 💥 数据流可视化辅助
        // ==========================================

        // 末端箭头 — 设计要求:
        //   1. tip 从 p4 向 from 方向回退 2px,不压在节点边框上(原版直接 tip = p4 看起来糊在边上)
        //   2. 长宽比 2:1 而不是接近 1:1 等边三角形(等边看着是方块)
        //   3. 写连线 = 实心三角(▶);读连线 = 空心 chevron(>),只画两笔不填充
        //   4. chevron 用 round line cap + round join,边缘平滑
        private static void DrawArrow(DrawingContext dc, Point tip, Point from, Brush fill, double size, bool hollow)
        {
            double dx = tip.X - from.X, dy = tip.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5) return;
            double ux = dx / len, uy = dy / len;
            double px = -uy, py = ux; // 90° 法线

            // tip 拉回 2px,跟节点边留出小空隙
            const double tipPullback = 2.0;
            Point arrowTip = new Point(tip.X - ux * tipPullback, tip.Y - uy * tipPullback);

            // 2:1 长宽比 — base 距 tip 沿轴向 size,两侧法向各偏 size*0.4
            double half = size * 0.4;
            Point baseLeft = new Point(arrowTip.X - ux * size + px * half, arrowTip.Y - uy * size + py * half);
            Point baseRight = new Point(arrowTip.X - ux * size - px * half, arrowTip.Y - uy * size - py * half);

            if (hollow)
            {
                // 读箭头:chevron 风格,base→tip 两笔,无填充
                var stroke = ChevronPen(fill);
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(baseLeft, false, false);
                    c.LineTo(arrowTip, true, false);
                    c.LineTo(baseRight, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, stroke, geo);
            }
            else
            {
                // 写箭头:实心三角,顺时针 tip→right→left
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(arrowTip, true, true);
                    c.LineTo(baseRight, true, false);
                    c.LineTo(baseLeft, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(fill, null, geo);
            }
        }

        private static readonly Dictionary<Brush, Pen> s_chevronPens = new();

        private static Pen ChevronPen(Brush fill)
        {
            if (s_chevronPens.TryGetValue(fill, out var pen)) return pen;
            pen = new Pen(fill, 1.6)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            s_chevronPens[fill] = pen;
            return pen;
        }

        // Bezier 中点处画 命中速率 + 累计数 — 选中节点时所有相关连线浮出。
        // 关键可读性细节:
        //  - 文字一律白色,不跟着 heat 颜色变 dim blue(原来低 heat 时 dim blue 字 + 深色 pill 几乎看不见)
        //  - pill bg 加大到 240 alpha + 12px 横向 padding + pipe 颜色边框,远观也能跟它对应的连线对得上
        //  - 字号 12 而非 11,更易读
        private void DrawLinkHitCount(DrawingContext dc, Point p1, Point p4, double cpX, TopoLink link, Brush color)
        {
            Point cp1 = new Point(p1.X + cpX, p1.Y);
            Point cp2 = new Point(p4.X - cpX, p4.Y);
            const double t = 0.5, u = 0.5;
            double mx = u * u * u * p1.X + 3 * u * u * t * cp1.X + 3 * u * t * t * cp2.X + t * t * t * p4.X;
            double my = u * u * u * p1.Y + 3 * u * u * t * cp1.Y + 3 * u * t * t * cp2.Y + t * t * t * p4.Y;
            DrawLinkHitCountAt(dc, new Point(mx, my), link, color);
        }

        private void DrawLinkHitCountAt(DrawingContext dc, Point mid, TopoLink link, Brush color)
        {
            double mx = mid.X, my = mid.Y;
            string text;
            if (link.RecentRate >= 1)
            {
                // 流速 ≥ 1/s → 显示 "23/s · 1.2k" 双指标格式
                int rateInt = (int)Math.Round(link.RecentRate);
                text = $"{HumanCount(rateInt)}/s · {HumanCount(link.LastHitCount)}";
            }
            else if (link.RecentRate <= 0)
            {
                // 停了:写明"静止",跟没有粒子的静止实线对上
                text = $"静止 · {HumanCount(link.LastHitCount)}";
            }
            else
            {
                // 流速很低(< 1/s) → 只显示累计总数
                text = HumanCount(link.LastHitCount);
            }

            if (link.Via.Count > 0)
            {
                // 折叠边:写出经过了哪些端口,不然看不出这两个 Feature 靠什么连着
                var names = link.Via.Select(id => _frameNodeDisplay.GetValueOrDefault(id) ?? id).ToList();
                string via = names.Count <= 2 ? string.Join(", ", names) : $"{names[0]}, {names[1]} 等 {names.Count} 个";
                text = $"经 {via} · {text}";
            }

            var ft = Text(text, _monoFont, 11, TextPrimaryBrush);
            double pillW = ft.Width + 14;
            double pillH = ft.Height + 6;
            dc.DrawRoundedRectangle(PillBgBrush, CachedPen(color, 1.5),
                new Rect(mx - pillW / 2, my - pillH / 2, pillW, pillH), 4, 4);
            DrawText(dc, ft, new Point(mx - ft.Width / 2, my - ft.Height / 2));
        }

        private static string HumanCount(int n)
        {
            if (n < 1000) return n.ToString();
            if (n < 10_000) return (n / 1000.0).ToString("F1") + "k";
            if (n < 1_000_000) return (n / 1000) + "k";
            return (n / 1_000_000.0).ToString("F1") + "M";
        }

        private const double IdleAfterSec = 3.0;

        // 距最近一次收到数据的秒数;从没收到过返回 null。图层 / 跨图镜像 / 环境没有持续的数据,不参与静默判断。
        private double? SecondsSinceLastHit(string nodeId, long nowTicks)
        {
#if DEBUG
            if (_tracer != null && _tracer.LastHitTicks.TryGetValue(nodeId, out long lastTicks))
                return (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
#endif
            return null;
        }

        private bool IsIdle(TopoNode node, long nowTicks)
        {
            var kind = KindOf(node);
            if (kind == NodeKind.Layer || (kind == NodeKind.Source && node.Id != "PIPE")) return false;
            var sec = SecondsSinceLastHit(node.Id, nowTicks);
            return sec.HasValue && sec.Value > IdleAfterSec;
        }

        private string ColumnLabel(int col) =>
            col >= _flowColumnCount ? "未连通"
            : col == 0 ? "源"
            : $"第 {col} 跳";

        private void DrawTierHeaders(DrawingContext dc)
        {
            // 跟 LayoutNodes 用同一份列划分,保证表头与节点列严格对齐。
            EnsureColumns();
            int colCount = ColumnCount;
            if (colCount == 0) return;

            double colW = _layout?.ColumnWidth ?? ActualWidth / colCount;
            double x0 = -_scrollX; // 跟随横向滚动
            // 相邻列交替一层很淡的色带,不靠顶上一行字也能看出"哪些节点在同一跳"
            double bandBottom = ActualHeight - TimelineHeight - LegendHeight;
            for (int i = 1; i < colCount; i += 2)
                dc.DrawRectangle(ColumnBandBrush, null, new Rect(x0 + i * colW, 0, colW, Math.Max(0, bandBottom)));
            var headerBrush = TextSecondaryBrush;
            for (int i = 0; i < colCount; i++)
            {
                var ft = Text(ColumnLabel(i), _font, 12, headerBrush);
                double cx = x0 + (i + 0.5) * colW;
                DrawText(dc, ft, new Point(cx - ft.Width / 2, 6));
            }

            // 表头下分隔线
            dc.DrawLine(CachedPen(SeparatorBrush, 1), new Point(0, HeaderHeight - 1), new Point(ActualWidth, HeaderHeight - 1));
        }

        // 底部图例条:两行,第一行讲"图上的东西是什么"(节点形状 / 箭头),第二行讲颜色。
        // 新人看图最先卡在"方框和胶囊有什么区别、实心空心箭头什么意思",所以形状放第一行。
        private void DrawLegend(DrawingContext dc)
        {
            double top = ActualHeight - LegendHeight;
            dc.DrawRectangle(TimelineBgBrush, null, new Rect(0, top, ActualWidth, LegendHeight));
            dc.DrawLine(CachedPen(SeparatorBrush, 1), new Point(0, top), new Point(ActualWidth, top));

            double x = 10;
            double row1 = top + 12, row2 = top + 32;

            void Label(string text, double y)
            {
                var ft = Text(text, _font, 11, TextSecondaryBrush);
                DrawText(dc, ft, new Point(x, y - ft.Height / 2));
                x += ft.Width + 18;
            }
            void Box(Brush fill, Brush border, double r, double y)
            {
                dc.DrawRoundedRectangle(fill, CachedPen(border, 1.2), new Rect(x, y - 7, 26, 14), r, r);
                x += 32;
            }
            void Line(Brush b, bool dashed, double y)
            {
                dc.DrawLine(CachedPen(b, 2, dashed), new Point(x, y), new Point(x + 24, y));
                x += 30;
            }
            void Arrow(bool hollow, double y)
            {
                dc.DrawLine(CachedPen(TextSecondaryBrush, 1.5), new Point(x, y), new Point(x + 16, y));
                DrawArrow(dc, new Point(x + 22, y), new Point(x, y), TextSecondaryBrush, 6, hollow);
                x += 30;
            }

            Box(SourceFillBrush, SourceAccentBrush, 3, row1); Label("数据源", row1);
            Box(_nodeBrush, CardBorderBrush, 3, row1);        Label("Feature", row1);
            Box(PortFillBrush, PortBorderBrush, 7, row1);     Label("端口(黑板上的一格数据)", row1);
            Box(LayerFillBrush, LayerBorderBrush, 7, row1);   Label("图层(最终画到屏上)", row1);
            Arrow(false, row1); Label("写入", row1);
            Arrow(true, row1);  Label("读取", row1);
            Label("列 = 离数据源几跳", row1);
            dc.DrawEllipse(ActiveDotBrush, null, new Point(x + 4, row1), 2.5, 2.5); x += 12;
            Label("绿点 = 1 秒内有数据", row1);

            x = 10;
            Line(DataLinkBrush, false, row2);                 Label("读写黑板", row2);
            Line(LayerLinkBrush, false, row2);                Label("推给图层", row2);
            Line(BackEdgeBrush, true, row2);                  Label("回写(环路,如视口联动,合法)", row2);
            Line(UpstreamBrush, false, row2);                 Label("选中后:上游", row2);
            Line(DownstreamBrush, false, row2);               Label("下游", row2);
            Label("线越粗 = 流量越大(选中后链路上有流动粒子)", row2);
            Label("虚线框 = 3 秒没收到数据(静默)", row2);
            Line(FlowBrush, false, row2);                     Label("触发流程(空格捕获)", row2);
        }

        private void DrawFlowParticle(DrawingContext dc, Point p1, Point p4, double cpX, double offset, double heat, bool isTargeted)
        {
            Point p2 = new Point(p1.X + cpX, p1.Y);
            Point p3 = new Point(p4.X - cpX, p4.Y);
            double[] offsets = { offset, (offset + 0.33) % 1.0, (offset + 0.66) % 1.0 };

            Brush particleBrush = isTargeted ? Brushes.White : Brushes.LightCyan;
            double radius = isTargeted ? 4.5 : 2.0 + (heat * 2.0); // 选中时粒子变大发白光

            foreach (var t in offsets)
            {
                double u = 1 - t;
                double x = u * u * u * p1.X + 3 * u * u * t * p2.X + 3 * u * t * t * p3.X + t * t * t * p4.X;
                double y = u * u * u * p1.Y + 3 * u * u * t * p2.Y + 3 * u * t * t * p3.Y + t * t * t * p4.Y;
                dc.DrawEllipse(particleBrush, null, new Point(x, y), radius, radius);
            }
        }

        private void LayoutNodes()
        {
            EnsureColumns();
            if (_layout == null) return;
            foreach (var n in _nodes)
            {
                if (n.IsPinned) continue; // 被用户拖动过的节点保持其 Bounds
                if (_layout.Boxes.TryGetValue(n.Id, out var b)) n.Bounds = new Rect(b.X, b.Y, b.W, b.H);
            }
        }

        // 布局算好的横竖折线;两端有被拖动过的节点时它已过时,返回 null 让调用方退回曲线
        private List<Point>? RouteFor(TopoLink link, TopoNode src, TopoNode tgt)
        {
            if (_layout == null || src.IsPinned || tgt.IsPinned) return null;
            if (!_layout.Routes.TryGetValue((link.Src, link.Tgt), out var r) || r.Count < 2) return null;
            var pts = new List<Point>(r.Count);
            foreach (var p in r) pts.Add(new Point(p.X, p.Y));
            return pts;
        }

        // 折线拐角用小圆角,比直角好跟线
        private static void AppendRoundedPolyline(StreamGeometryContext ctx, List<Point> pts, double radius)
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                var c = pts[i + 1];
                double r = Math.Min(radius, Math.Min((b - a).Length, (c - b).Length) / 2);
                ctx.LineTo(b + Unit(a - b) * r, true, false);
                ctx.QuadraticBezierTo(b, b + Unit(c - b) * r, true, false);
            }
            ctx.LineTo(pts[pts.Count - 1], true, false);
        }

        private static Vector Unit(Vector v) => v.Length < 1e-9 ? new Vector(0, 0) : v / v.Length;

        // 折线上按长度比例 t ∈ [0,1] 取点
        private static Point PointAlong(List<Point> pts, double t)
        {
            double total = 0;
            for (int i = 0; i + 1 < pts.Count; i++) total += (pts[i + 1] - pts[i]).Length;
            double target = Math.Max(0, Math.Min(1, t)) * total;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double len = (pts[i + 1] - pts[i]).Length;
                if (target <= len || i + 2 == pts.Count)
                    return len < 1e-9 ? pts[i] : pts[i] + (pts[i + 1] - pts[i]) * (target / len);
                target -= len;
            }
            return pts[pts.Count - 1];
        }

        private void DrawFlowParticleAlong(DrawingContext dc, List<Point> pts, double offset, double heat, bool isTargeted)
        {
            Brush particleBrush = isTargeted ? Brushes.White : Brushes.LightCyan;
            double radius = isTargeted ? 4.5 : 2.0 + (heat * 2.0);
            foreach (var t in new[] { offset, (offset + 0.33) % 1.0, (offset + 0.66) % 1.0 })
                dc.DrawEllipse(particleBrush, null, PointAlong(pts, t), radius, radius);
        }

        // ─── 每帧绘制的缓存 ───
        // 监控每帧整张重画(时间线、粒子、静默状态都随时间变),但文字、画刷、画笔绝大多数帧帧相同。
        // 以前每帧为每个节点 / 每行时间线 new FormattedText / Pen / SolidColorBrush,一帧分配上 MB,
        // 文字排版占掉一半帧时间。文字排好后存成冻结的 DrawingGroup,下一帧直接引用。
        private sealed class CachedText
        {
            public readonly Drawing Drawing;
            public readonly double Width;
            public readonly double Height;
            public CachedText(Drawing drawing, double width, double height) { Drawing = drawing; Width = width; Height = height; }
        }

        private readonly struct TextKey : IEquatable<TextKey>
        {
            private readonly string _text;
            private readonly Typeface _face;
            private readonly double _size;
            private readonly Brush _brush;
            private readonly double _maxWidth;
            public TextKey(string text, Typeface face, double size, Brush brush, double maxWidth)
            { _text = text; _face = face; _size = size; _brush = brush; _maxWidth = maxWidth; }
            public bool Equals(TextKey o) => _text == o._text && ReferenceEquals(_face, o._face) && _size == o._size
                && ReferenceEquals(_brush, o._brush) && _maxWidth == o._maxWidth;
            public override bool Equals(object? obj) => obj is TextKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(_text, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_face),
                _size, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_brush), _maxWidth);
        }

        // 两代缓存:本帧用到的进 _textNow;帧末整代轮换,上一帧之后再没用过的文字(计时、计数这类每帧在变的)随之丢弃,缓存不会越积越多。
        private Dictionary<TextKey, CachedText> _textNow = new();
        private Dictionary<TextKey, CachedText> _textPrev = new();

        // brush 必须是跨帧复用的同一实例(静态冻结画刷),否则每帧都对不上缓存。
        private CachedText Text(string text, Typeface face, double size, Brush brush, double maxWidth = 0)
        {
            var key = new TextKey(text, face, size, brush, maxWidth);
            if (_textNow.TryGetValue(key, out var t)) return t;
            if (_textPrev.Remove(key, out t))
            {
                _textNow[key] = t;
                return t;
            }
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
            if (maxWidth > 0)
            {
                ft.MaxTextWidth = maxWidth;
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }
            var group = new DrawingGroup();
            using (var ctx = group.Open()) ctx.DrawText(ft, new Point(0, 0));
            group.Freeze();
            t = new CachedText(group, ft.Width, ft.Height);
            _textNow[key] = t;
            return t;
        }

        private static void DrawText(DrawingContext dc, CachedText t, Point origin)
        {
            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.DrawDrawing(t.Drawing);
            dc.Pop();
        }

        private void EndFrameTextCache()
        {
            var old = _textPrev;
            old.Clear();
            _textPrev = _textNow;
            _textNow = old;
        }

        private static readonly Dictionary<(Brush, double, bool), Pen> s_pens = new();

        // 画笔只有 颜色 × 粗细 × 虚实 几十种组合,冻结后全局复用
        private static Pen CachedPen(Brush brush, double thickness, bool dashed = false)
        {
            var key = (brush, thickness, dashed);
            if (s_pens.TryGetValue(key, out var pen)) return pen;
            pen = new Pen(brush, thickness);
            if (dashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            s_pens[key] = pen;
            return pen;
        }

        private static readonly Dictionary<(Brush, double), Brush> s_dimmed = new();

        private static Brush DimmedBrush(Brush brush, double opacity)
        {
            var key = (brush, opacity);
            if (s_dimmed.TryGetValue(key, out var dim)) return dim;
            dim = brush.CloneCurrentValue();
            dim.Opacity = brush.Opacity * opacity;
            dim.Freeze();
            s_dimmed[key] = dim;
            return dim;
        }

        private static readonly Dictionary<uint, SolidColorBrush> s_colorBrushes = new();

        // 时间线活动柱的颜色随强度连续变化,但分量是 byte,组合有限,按 ARGB 缓存
        private static SolidColorBrush CachedBrush(byte a, byte r, byte g, byte b)
        {
            uint key = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
            if (s_colorBrushes.TryGetValue(key, out var br)) return br;
            br = Frozen(a, r, g, b);
            s_colorBrushes[key] = br;
            return br;
        }

        // 节点 id → 节点、每个节点的出边 / 入边(按对端高度排好),每帧重建但复用容器。
        // 以前连线循环里对每条边 _nodes.FirstOrDefault,再用 LINQ GroupBy 建分组,O(边 × 节点)且帧帧分配。
        private readonly Dictionary<string, TopoNode> _nodeById = new();
        private readonly Dictionary<string, List<TopoLink>> _srcGroups = new();
        private readonly Dictionary<string, List<TopoLink>> _tgtGroups = new();
        private readonly Stack<List<TopoLink>> _groupPool = new();

        private void BuildNodeIndex()
        {
            _nodeById.Clear();
            foreach (var n in _nodes) _nodeById.TryAdd(n.Id, n); // 重复 id 取第一个,跟 FirstOrDefault 一致
        }

        private void BuildPortGroups()
        {
            RecycleGroups(_srcGroups);
            RecycleGroups(_tgtGroups);
            foreach (var l in _links)
            {
                AddToGroup(_srcGroups, l.Src, l);
                AddToGroup(_tgtGroups, l.Tgt, l);
            }
            foreach (var g in _srcGroups.Values) SortByTop(g, bySrc: false);
            foreach (var g in _tgtGroups.Values) SortByTop(g, bySrc: true);
        }

        private void RecycleGroups(Dictionary<string, List<TopoLink>> groups)
        {
            foreach (var g in groups.Values) { g.Clear(); _groupPool.Push(g); }
            groups.Clear();
        }

        private void AddToGroup(Dictionary<string, List<TopoLink>> groups, string key, TopoLink link)
        {
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = _groupPool.Count > 0 ? _groupPool.Pop() : new List<TopoLink>();
            g.Add(link);
        }

        // 稳定插入排序(组很小):对端高度相同的保持原连线顺序,跟原先 OrderBy 的结果一致
        private void SortByTop(List<TopoLink> g, bool bySrc)
        {
            for (int i = 1; i < g.Count; i++)
            {
                var item = g[i];
                double key = OtherEndTop(item, bySrc);
                int j = i - 1;
                while (j >= 0 && OtherEndTop(g[j], bySrc) > key)
                {
                    g[j + 1] = g[j];
                    j--;
                }
                g[j + 1] = item;
            }
        }

        private double OtherEndTop(TopoLink l, bool bySrc)
            => _nodeById.TryGetValue(bySrc ? l.Src : l.Tgt, out var n) ? n.Bounds.Top : 0;

        private static readonly SolidColorBrush FlowNumberBrush = Frozen(255, 6, 40, 30);
        private static readonly SolidColorBrush SeparatorBrush = Frozen(255, 30, 41, 59);
        private static readonly SolidColorBrush CalloutBgBrush = Frozen(245, 30, 41, 59);
        private static readonly SolidColorBrush PillBgBrush = Frozen(245, 15, 23, 42);
        private static readonly SolidColorBrush FlowPanelBgBrush = Frozen(235, 15, 23, 42);
        private static readonly SolidColorBrush ScrollThumbBrush = Frozen(140, 100, 116, 139);
        private static readonly SolidColorBrush TimelineBgBrush = Frozen(255, 11, 18, 32);
        private static readonly SolidColorBrush TimelineSepBrush = Frozen(255, 40, 40, 48);
        private static readonly SolidColorBrush TimelineTitleBrush = Frozen(255, 200, 210, 220);
        private static readonly SolidColorBrush TimelineTickBrush = Frozen(255, 60, 65, 75);
        private static readonly SolidColorBrush TimelineAxisLabelBrush = Frozen(255, 140, 150, 160);
        private static readonly SolidColorBrush TimelineKeyLabelBrush = Frozen(255, 180, 190, 200);
        private static readonly SolidColorBrush TimelineEmptyBrush = Frozen(255, 110, 115, 125);
        private static readonly SolidColorBrush ZebraOddBrush = Frozen(255, 22, 22, 28);
        private static readonly SolidColorBrush ZebraEvenBrush = Frozen(255, 28, 28, 36);
        private static readonly SolidColorBrush LabelColumnBrush = Frozen(255, 20, 22, 28);
        private static readonly SolidColorBrush LaneSepBrush = Frozen(35, 100, 110, 130);
        private static readonly SolidColorBrush LaneSelectedBrush = Frozen(60, 100, 180, 255);
        private static readonly SolidColorBrush LaneQuietBrush = Frozen(255, 240, 190, 80);
        private static readonly SolidColorBrush LaneSelectedTextBrush = Frozen(255, 160, 210, 255);
        private static readonly SolidColorBrush LaneTextBrush = Frozen(255, 210, 218, 226);
        private static readonly SolidColorBrush LabelColumnSepBrush = Frozen(80, 80, 90, 110);
        private static readonly SolidColorBrush PinDotBrush = Frozen(255, 255, 200, 100);
        private static readonly SolidColorBrush BadgeHotBrush = Frozen(255, 248, 113, 113);

        private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        private static readonly SolidColorBrush UpstreamBrush   = Frozen(255, 251, 191, 36);  // 来源:琥珀金 #FBBF24
        private static readonly SolidColorBrush DownstreamBrush = Frozen(255, 56, 189, 248);  // 去向:天蓝 #38BDF8
        private static readonly SolidColorBrush BackEdgeBrush   = Frozen(220, 245, 158, 11);  // 回写:橙色虚线 #F59E0B
        private static readonly SolidColorBrush PortBorderBrush = Frozen(255, 43, 58, 82);
        private static readonly SolidColorBrush PortFillBrush   = Frozen(255, 19, 28, 46);
        private static readonly SolidColorBrush SourceFillBrush = Frozen(255, 27, 35, 54);
        private static readonly SolidColorBrush LayerFillBrush  = Frozen(255, 27, 35, 54);
        private static readonly SolidColorBrush LayerBorderBrush = Frozen(255, 167, 139, 250); // 图层紫 #A78BFA
        private static readonly SolidColorBrush DataLinkBrush   = Frozen(255, 82, 96, 119);    // 读写黑板的普通数据边,中性灰蓝
        private static readonly SolidColorBrush LayerLinkBrush  = Frozen(200, 167, 139, 250);  // Feature → 图层
        private static readonly SolidColorBrush CardBorderBrush = Frozen(255, 51, 65, 85);     // #334155
        private static readonly SolidColorBrush CardSelectedBrush = Frozen(255, 36, 49, 77);
        private static readonly SolidColorBrush SelectedBorderBrush = Frozen(255, 226, 232, 240);
        private static readonly SolidColorBrush SourceAccentBrush = Frozen(255, 34, 197, 94);  // 数据源绿 #22C55E
        private static readonly SolidColorBrush FeatureAccentBrush = Frozen(255, 100, 116, 139);
        private static readonly SolidColorBrush TextPrimaryBrush = Frozen(255, 226, 232, 240);  // #E2E8F0
        private static readonly SolidColorBrush TextSecondaryBrush = Frozen(255, 148, 163, 184); // #94A3B8
        private static readonly SolidColorBrush TextMutedBrush = Frozen(255, 100, 116, 139);    // #64748B
        private static readonly SolidColorBrush ColumnBandBrush = Frozen(255, 18, 27, 47);      // 相邻列交替色带
        private static readonly SolidColorBrush ActiveDotBrush = Frozen(220, 34, 197, 94);      // 活跃绿点
        private static readonly SolidColorBrush FlowBrush = Frozen(255, 52, 211, 153);          // 触发流程:翠绿 #34D399

        private const double ActiveWithinSec = 1.0;

        // ─── 触发流程 ───
        // 数据持续流动时"哪些被触发了"一直是"全部",看不出一次推送怎么一路传下去。空格捕获一波
        // (追踪器从第一次写入起录 100ms),按首次出现顺序把读写边排好,逐步回放:节点标先后序号,
        // 右上角列出步骤清单。Feature 推给图层的 trait 捕获不到,按"被触发的 Feature 挂着的图层"补在最后。
        private sealed class FlowEdge
        {
            public string Src = "";
            public string Tgt = "";
            public bool IsWrite;
            public bool ToLayer;
            public double Ms;
        }

        private List<FlowEdge>? _flow;
        private readonly Dictionary<string, int> _flowNodeOrder = new();
        private readonly HashSet<string> _flowRevealedNodes = new();
        private readonly HashSet<(string, string)> _flowRevealedEdges = new();
        private long _flowReplayStart;
        private double _flowStepDelaySec;
        private double _flowTotalMs;
        private int _flowRevealed;

        // 回放时粒子的相位:跟流量无关,固定速度(约 1.6 秒走完一条边)
        private double FlowParticlePhase()
            => ((Stopwatch.GetTimestamp() - _flowReplayStart) / (double)Stopwatch.Frequency * 0.6) % 1.0;

        private void ClearFlow()
        {
            _flow = null;
            _flowNodeOrder.Clear();
            _flowRevealedNodes.Clear();
            _flowRevealedEdges.Clear();
            _flowRevealed = 0;
        }

#if DEBUG
        private void BuildFlow(List<TopologyTracer.FlowStep> steps)
        {
            ClearFlow();
            var edges = new List<FlowEdge>();
            var seen = new HashSet<(string, string)>();
            int order = 0;
            void Touch(string id) { if (!_flowNodeOrder.ContainsKey(id)) _flowNodeOrder[id] = ++order; }
            double freqMs = Stopwatch.Frequency / 1000.0;
            foreach (var st in steps)
            {
                Touch(st.Src);
                Touch(st.Tgt);
                if (seen.Add((st.Src, st.Tgt)))
                    edges.Add(new FlowEdge { Src = st.Src, Tgt = st.Tgt, IsWrite = st.IsWrite, Ms = st.Ticks / freqMs });
            }
            _flowTotalMs = steps.Count > 0 ? steps[steps.Count - 1].Ticks / freqMs : 0;
            foreach (var l in _modelLinks.Where(l => l.ToLayer && _flowNodeOrder.ContainsKey(l.Src)).ToList())
            {
                if (!seen.Add((l.Src, l.Tgt))) continue;
                Touch(l.Tgt);
                edges.Add(new FlowEdge { Src = l.Src, Tgt = l.Tgt, IsWrite = true, ToLayer = true, Ms = _flowTotalMs });
            }
            _flow = edges;
            // 回放总长约 4 秒,步数少时每步最多 0.25 秒
            _flowStepDelaySec = Math.Min(0.25, 4.0 / Math.Max(1, edges.Count));
            _flowReplayStart = Stopwatch.GetTimestamp();
        }
#endif

        // 按回放进度更新"已亮"的边和节点,每帧调一次
        private void UpdateFlowReveal()
        {
            if (_flow == null) return;
            double t = (Stopwatch.GetTimestamp() - _flowReplayStart) / (double)Stopwatch.Frequency;
            int k = Math.Min(_flow.Count, (int)Math.Floor(t / _flowStepDelaySec) + 1);
            for (int i = _flowRevealed; i < k; i++)
            {
                var e = _flow[i];
                _flowRevealedEdges.Add((e.Src, e.Tgt));
                _flowRevealedNodes.Add(e.Src);
                _flowRevealedNodes.Add(e.Tgt);
            }
            _flowRevealed = k;
        }

        // 折叠视图里的边是"Feature → Feature",在捕获里对不上原始边:两端都已亮就算经过
        private bool FlowEdgeRevealed(TopoLink link)
            => _flowRevealedEdges.Contains((link.Src, link.Tgt))
               || (link.Via.Count > 0 && _flowRevealedNodes.Contains(link.Src) && _flowRevealedNodes.Contains(link.Tgt));

        private string FlowName(string id)
        {
            var node = _modelNodes.FirstOrDefault(n => n.Id == id);
            return node != null ? DisplayLabelOf(node) : _frameNodeDisplay.GetValueOrDefault(id) ?? id;
        }

        // 右上角步骤清单:第几步、谁 → 谁、读还是写、距写入多少毫秒;当前回放到的那步高亮
        private void DrawFlowPanel(DrawingContext dc)
        {
            if (_flow == null || _flow.Count == 0) return;
            const int maxLines = 26;
            const double lineH = 16, pad = 8, width = 430;
            int lines = Math.Min(maxLines, _flow.Count);
            double height = pad * 2 + 20 + lines * lineH + (_flow.Count > maxLines ? lineH : 0);
            var vp = GraphViewport;
            var rect = new Rect(vp.Right - width - 12, vp.Top + 6, width, Math.Min(height, vp.Height - 12));
            dc.DrawRoundedRectangle(FlowPanelBgBrush, CachedPen(CardBorderBrush, 1), rect, 6, 6);
            dc.PushClip(new RectangleGeometry(rect));

            var title = Text($"触发流程 · {_flow.Count} 步 · {_flowTotalMs:F1}ms", _fontStrong, 12, TextPrimaryBrush);
            DrawText(dc, title, new Point(rect.X + pad, rect.Y + pad));

            // 当前回放步附近的窗口:步数多时让高亮那步保持在清单里
            int start = Math.Max(0, Math.Min(_flowRevealed - maxLines / 2, _flow.Count - maxLines));
            for (int i = 0; i < lines; i++)
            {
                int idx = start + i;
                if (idx >= _flow.Count) break;
                var e = _flow[idx];
                bool revealed = idx < _flowRevealed;
                bool current = idx == _flowRevealed - 1;
                string verb = e.ToLayer ? "推给图层" : e.IsWrite ? "写" : "读";
                string line = $"{idx + 1,3}. {FlowName(e.Src)} → {FlowName(e.Tgt)}   {verb}   +{e.Ms:F2}ms";
                var ft = Text(line, _font, 11, current ? FlowBrush : revealed ? TextPrimaryBrush : TextMutedBrush,
                    maxWidth: width - pad * 2);
                DrawText(dc, ft, new Point(rect.X + pad, rect.Y + pad + 20 + i * lineH));
            }
            if (_flow.Count > maxLines)
            {
                var more = Text($"…共 {_flow.Count} 步", _font, 11, TextMutedBrush);
                DrawText(dc, more, new Point(rect.X + pad, rect.Y + pad + 20 + lines * lineH));
            }
            dc.Pop();
        }

        // 选中节点的上游(含自身)/ 下游(含自身)节点集合,沿去掉回边后的前驱 / 后继表 BFS。
        // 走回边的话环里一个点就能把整个环连同上下游全拉进来,高亮失去意义。
        private readonly HashSet<string> _upstream = new();
        private readonly HashSet<string> _downstream = new();

        private void ComputeLineage()
        {
            _upstream.Clear();
            _downstream.Clear();
            if (_selectedId == null) return;
            Walk(_selectedId, _fwdPred, _upstream);
            Walk(_selectedId, _fwdSucc, _downstream);

            static void Walk(string start, Dictionary<string, List<string>> next, HashSet<string> seen)
            {
                var stack = new Stack<string>();
                stack.Push(start);
                seen.Add(start);
                while (stack.Count > 0)
                {
                    var v = stack.Pop();
                    if (!next.TryGetValue(v, out var ns)) continue;
                    foreach (var w in ns) if (seen.Add(w)) stack.Push(w);
                }
            }
        }

        private string DisplayLabelOf(TopoNode node) =>
            _frameNodeDisplay.GetValueOrDefault(node.Id) ?? node.Label;

        private static string KindText(NodeKind k) => k switch
        {
            NodeKind.Source => "数据源",
            NodeKind.Feature => "Feature",
            NodeKind.Layer => "图层",
            _ => "端口",
        };

        // 悬停(没有悬停时取选中)节点的全名气泡:节点里的名字可能被截成省略号。
        private void DrawNodeCallout(DrawingContext dc)
        {
            var id = _hoverId ?? _selectedId;
            if (id == null || !_nodeById.TryGetValue(id, out var node)) return;

            var kind = KindOf(node);
            int ins = _fwdPred.TryGetValue(node.Id, out var p) ? p.Count : 0;
            int outs = _fwdSucc.TryGetValue(node.Id, out var q) ? q.Count : 0;
            var since = SecondsSinceLastHit(node.Id, Stopwatch.GetTimestamp());
            string freshness = kind == NodeKind.Layer ? "" : since.HasValue
                ? (since.Value > IdleAfterSec ? $" · 静默 {since.Value:F0}s" : $" · 最近数据 {since.Value:F1}s 前")
                : " · 尚未收到数据";
            string text = $"{DisplayLabelOf(node)}\n{KindText(kind)} · 上游 {ins} · 下游 {outs}{freshness}";

            var ft = Text(text, _font, 11, Brushes.White);
            double x = Math.Min(Math.Max(4, node.Bounds.Left), Math.Max(4, ActualWidth - ft.Width - 16));
            double y = node.Bounds.Top - ft.Height - 12;
            if (y < HeaderHeight) y = node.Bounds.Bottom + 14;
            var rect = new Rect(x, y, ft.Width + 12, ft.Height + 6);
            dc.DrawRoundedRectangle(CalloutBgBrush, CachedPen(CardBorderBrush, 1), rect, 6, 6);
            DrawText(dc, ft, new Point(x + 6, y + 3));
        }

        // 选中后在表头下方一行说明:上下游各几个节点,颜色分别是什么。
        private void DrawSelectionSummary(DrawingContext dc)
        {
            string text;
#if DEBUG
            if (_tracer?.IsCaptureWaiting == true)
            {
                text = "等待下一次数据写入,捕获这一波触发…(Esc 取消)";
            }
            else
#endif
            if (_flow != null)
            {
                text = $"触发流程:{_flowNodeOrder.Count} 个节点、{_flow.Count} 步,数据源写入后 {_flowTotalMs:F1}ms 内完成 · 绿色序号 = 被触发的先后 · 空格重新捕获 · Esc 退出";
            }
            else if (_selectedId == null)
            {
                text = (_foldPorts ? "只看 Feature(端口已收进连线,按 F 展开)" : "完整视图(按 F 只看 Feature)")
                     + " · 空格捕获一次触发流程 · 点击节点看完整上下游 · 悬停看全名 · 拖动固定位置,右键取消 · 滚轮 / Shift+滚轮滚动 · T 折叠时间线 · Ctrl+E 导出";
            }
            else
            {
                string name = _nodeById.TryGetValue(_selectedId, out var node) ? DisplayLabelOf(node) : _selectedId;
                text = $"选中 {name}:金色 = 上游 {_upstream.Count - 1} 个节点(数据从哪来),蓝色 = 下游 {_downstream.Count - 1} 个节点(数据到哪去) · 点空白取消";
            }
            var ft = Text(text, _font, 11, TextSecondaryBrush);
            DrawText(dc, ft, new Point(8, HeaderHeight + 2));
        }

        // 三类节点画法不同:数据源(绿)、Feature(方框)、端口(胶囊,矮一号)。
        // 以前三类同一个方框,图上分不出"谁在干活"和"数据放在哪"。
        private enum NodeKind { Source, Feature, Port, Layer }
        private const double FeatureNodeH = 30;
        private const double PortNodeH = 22;

        // "PIPE" / "MIRROR" / "ENV" 即 TopologyTracer 的 PIPE_ID / MIRROR_ID / ENV_ID:数据源、跨图镜像、环境三种非 Feature 写入方。
        // Tracer 只在 DEBUG 下有成员,这里写字面量免得 Release 编不过。
        private static NodeKind KindOf(TopoNode n) =>
            n.Id == "PIPE" || n.Id == "MIRROR" || n.Id == "ENV" ? NodeKind.Source
            : n.Id.StartsWith("L_", StringComparison.Ordinal) ? NodeKind.Layer
            : n.Id.StartsWith("F_", StringComparison.Ordinal) ? NodeKind.Feature
            : NodeKind.Port;

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visual;
    }

    public class TopoNode
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public int Tier { get; set; }
        public Rect Bounds { get; set; }

        // 用户拖动后置 true,LayoutNodes 不再覆盖它的位置;右键单击节点解除。
        public bool IsPinned { get; set; }
    }
    public class TopoLink
    {
        public string Src { get; set; } = "";
        public string Tgt { get; set; } = "";
        public int HitCount { get; set; }
        public int LastHitCount { get; set; }
        public int LastDelta { get; set; }
        public double CurrentHeat { get; set; }
        public double ParticleOffset { get; set; }

        // 速率追踪 — 累计 hit 数对人没用(看不出"现在还在不在动"),
        // 用 Stopwatch 间隔算实时 hits/sec 并 EMA 平滑,UI 可显示"23/s"反映当前流速。
        public long LastSampleTicks { get; set; }
        public double RecentRate { get; set; }

        // 流速统计窗口:窗口起点(Stopwatch ticks)与窗口内累计命中数
        public long RateWindowStart { get; set; }
        public int RateWindowHits { get; set; }
        // 线宽档位 0..3
        public int WidthStep { get; set; }

        // Feature → 图层:trait 不经过黑板,命中数取 Feature 的 OnProject 次数
        public bool ToLayer { get; set; }

        // 折叠视图里 Feature → Feature 的边经过的端口 id;完整视图里为空
        public List<string> Via { get; } = new();
    }
}
