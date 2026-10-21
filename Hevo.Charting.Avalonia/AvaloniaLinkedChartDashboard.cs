using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hevo.Charting.Core;
using Hevo.Charting.Linked;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 列式联动看板的 Avalonia 版(对应 WPF 的 LinkedChartDashboard,API 一致):主图 + N 副图垂直堆叠,共享视口与命中。
    /// <para>
    /// 联动逻辑全在核心:<see cref="SchemaContext"/> 给每个 cell 注入"主图 / 副图"上下文(副图摘掉视口管家和悬浮窗),
    /// <see cref="LinkedChartContext"/> 在各 schema 的数据板就绪时挂上端口镜像桥。本控件只管布局和分隔条。
    /// </para>
    /// <para>
    /// pane 高度:<see cref="IsPaneResizeEnabled"/> 打开后(默认关)相邻 pane 之间有 <see cref="AvaloniaPaneSplitter"/>,拖动只改两侧 pane;
    /// 高度以比例保存(<see cref="GetPaneRatios"/> / <see cref="TrySetPaneRatios"/>),窗口缩放时按比例重排;
    /// 双击分隔条或 <see cref="ResetPaneRatios"/> 回到装配时给的 heightRatio。
    /// </para>
    /// </summary>
    /// <remarks>继承 Decorator 而不是 UserControl:不依赖主题提供模板,没装主题(无窗口测试、精简宿主)时内容照样进可视树。</remarks>
    public class AvaloniaLinkedChartDashboard : Decorator, LowCode.Designer.GraphViewer.ILinkedChartDashboard
    {
        public LinkedChartContext Context { get; }

        private readonly Grid _grid = new();
        private readonly List<PaneSlot> _slots = new();
        private readonly List<AvaloniaPaneSplitter> _splitters = new();

        private sealed class PaneSlot
        {
            public PaneSlot(ReactiveSchema schema, AvaloniaChartCell cell, double defaultRatio)
            {
                Schema = schema;
                Cell = cell;
                DefaultRatio = defaultRatio;
                Ratio = defaultRatio;
            }

            public ReactiveSchema Schema { get; }
            public AvaloniaChartCell Cell { get; }
            public double DefaultRatio { get; }
            public double Ratio { get; set; }
            public RowDefinition Row { get; } = new();
        }

        // 拖拽起点快照:位移按起点算,中途钳制不会累积误差
        private int _dragBoundary = -1;
        private double _dragUpperPx, _dragLowerPx, _dragUpperRatio, _dragLowerRatio;
        private bool _dragChanged;

        private bool _paneResizeEnabled;
        private double _minPaneHeight = 48;
        private double _splitterThickness = 5;
        private IBrush _splitterBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        private IBrush _splitterHoverBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x80, 0x80, 0x80));

        public AvaloniaLinkedChartDashboard(LinkedChartContext ctx)
        {
            Context = ctx;
            Child = _grid;
        }

        /// <summary>当前所有 cell 的只读快照,顺序 = AddMaster / AddPane / AddRaw 加入顺序。</summary>
        public IReadOnlyList<AvaloniaChartCell> Cells
        {
            get
            {
                var list = new List<AvaloniaChartCell>(_slots.Count);
                for (int i = 0; i < _slots.Count; i++) list.Add(_slots[i].Cell);
                return list;
            }
        }

        /// <summary>当前分隔条快照,第 i 个位于第 i 和 i+1 个 pane 之间。</summary>
        public IReadOnlyList<AvaloniaPaneSplitter> Splitters => _splitters.ToArray();

        /// <summary>是否在 pane 之间放可拖拽的分隔条。默认关(同 WPF 版)。</summary>
        public bool IsPaneResizeEnabled
        {
            get => _paneResizeEnabled;
            set
            {
                if (_paneResizeEnabled == value) return;
                CancelSplitterDrag();
                _paneResizeEnabled = value;
                RebuildRows();
            }
        }

        /// <summary>拖拽时单个 pane 至少保留的高度(DIP);分隔条打开时也作为行的 MinHeight。</summary>
        public double MinPaneHeight
        {
            get => _minPaneHeight;
            set
            {
                if (!(value >= 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
                _minPaneHeight = value;
                if (!_paneResizeEnabled) return;
                for (int i = 0; i < _slots.Count; i++) _slots[i].Row.MinHeight = value;
            }
        }

        /// <summary>分隔条占的行高(DIP),即可拖拽命中区;可见的只是中间 1px 线。</summary>
        public double SplitterThickness
        {
            get => _splitterThickness;
            set
            {
                if (!(value >= 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
                _splitterThickness = value;
                RefreshSplitterLook();
            }
        }

        /// <summary>分隔线颜色(常态)。</summary>
        public IBrush SplitterBrush
        {
            get => _splitterBrush;
            set { _splitterBrush = value ?? throw new ArgumentNullException(nameof(value)); RefreshSplitterLook(); }
        }

        /// <summary>分隔线颜色(悬停 / 拖拽中)。</summary>
        public IBrush SplitterHoverBrush
        {
            get => _splitterHoverBrush;
            set { _splitterHoverBrush = value ?? throw new ArgumentNullException(nameof(value)); RefreshSplitterLook(); }
        }

        /// <summary>pane 比例被用户或 API 改动后触发(拖拽松手、双击复位、TrySetPaneRatios、ResetPaneRatios);拖拽过程中不触发。</summary>
        public event EventHandler? PaneRatiosChanged;

        // ─── 装配期 API ────────────────────────────────────────────────────

        /// <summary>加主图。<b>必须最先且只能一次</b>。主图独占视口管家、悬浮窗、心跳 / 分页等。</summary>
        public AvaloniaLinkedChartDashboard AddMaster(ReactiveSchema schema, double heightRatio = 3)
        {
            if (_slots.Count > 0)
                throw new InvalidOperationException("Master 必须最先加,且只能一个");
            return AppendSlot(schema, heightRatio, SchemaContext.LinkedMaster(Context));
        }

        /// <summary>加副图。装饰钩子自动摘除视口管家 / 悬浮窗,视口跟主图走。</summary>
        public AvaloniaLinkedChartDashboard AddPane(ReactiveSchema schema, double heightRatio = 1)
            => AppendSlot(schema, heightRatio, SchemaContext.LinkedPane(Context));

        /// <summary>加 raw schema(<b>不联动</b>,纯堆叠)。</summary>
        public AvaloniaLinkedChartDashboard AddRaw(ReactiveSchema schema, double heightRatio = 1)
            => AppendSlot(schema, heightRatio, attached: null);

        // 蓝图装配器(DashboardAssembler)按角色加 cell
        void LowCode.Designer.GraphViewer.ILinkedChartDashboard.AddCell(LowCode.Designer.DashboardCellRole role, ReactiveSchema schema, double heightRatio)
        {
            switch (role)
            {
                case LowCode.Designer.DashboardCellRole.Master: AddMaster(schema, heightRatio); break;
                case LowCode.Designer.DashboardCellRole.Pane: AddPane(schema, heightRatio); break;
                case LowCode.Designer.DashboardCellRole.Raw: AddRaw(schema, heightRatio); break;
            }
        }

        IReadOnlyList<IChartCell> LowCode.Designer.GraphViewer.ILinkedChartDashboard.CellList => Cells;

        // ─── 运行期 API ────────────────────────────────────────────────────

        /// <summary>运行期热加副图,挂上立即纳入联动。</summary>
        public void AppendPaneAtRuntime(ReactiveSchema schema, double heightRatio = 1)
            => AppendSlot(schema, heightRatio, SchemaContext.LinkedPane(Context));

        /// <summary>运行期热删副图。<b>主图(行 0)不允许删</b>。</summary>
        public bool RemovePaneAtRuntime(ReactiveSchema schema)
        {
            int idx = _slots.FindIndex(s => ReferenceEquals(s.Schema, schema));
            if (idx <= 0) return false;

            CancelSplitterDrag();
            _grid.Children.Remove(_slots[idx].Cell);
            _slots.RemoveAt(idx);
            RebuildRows();
            return true;
        }

        // ─── pane 高度 ─────────────────────────────────────────────────────

        /// <summary>当前各 pane 的高度比例,顺序同 <see cref="Cells"/>,总和归一为 1。</summary>
        public double[] GetPaneRatios()
        {
            var result = new double[_slots.Count];
            double sum = 0;
            for (int i = 0; i < _slots.Count; i++) sum += _slots[i].Ratio;
            for (int i = 0; i < _slots.Count; i++) result[i] = sum > 0 ? _slots[i].Ratio / sum : 0;
            return result;
        }

        /// <summary>恢复保存过的比例。个数必须等于当前 pane 数,每项为正有限数;不合法返回 false 并保持原状。</summary>
        public bool TrySetPaneRatios(IReadOnlyList<double> ratios)
        {
            if (ratios is null || ratios.Count != _slots.Count || _slots.Count == 0) return false;
            double sum = 0;
            for (int i = 0; i < ratios.Count; i++)
            {
                double r = ratios[i];
                if (!(r > 0) || double.IsInfinity(r)) return false;
                sum += r;
            }
            CancelSplitterDrag();
            // 换算回 heightRatio 的量纲:之后 AppendPaneAtRuntime(heightRatio: 1) 才和现有 pane 可比
            double scale = 0;
            for (int i = 0; i < _slots.Count; i++) scale += _slots[i].DefaultRatio;
            if (!(scale > 0)) scale = 1;
            for (int i = 0; i < _slots.Count; i++) _slots[i].Ratio = ratios[i] / sum * scale;
            ApplyRatios();
            PaneRatiosChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>回到装配时 AddMaster / AddPane / AddRaw 给的 heightRatio。</summary>
        public void ResetPaneRatios()
        {
            CancelSplitterDrag();
            for (int i = 0; i < _slots.Count; i++) _slots[i].Ratio = _slots[i].DefaultRatio;
            ApplyRatios();
            PaneRatiosChanged?.Invoke(this, EventArgs.Empty);
        }

        // 拖拽三段由 AvaloniaPaneSplitter 驱动;测试直接调

        internal bool BeginSplitterDrag(int boundaryIndex)
        {
            if (boundaryIndex < 0 || boundaryIndex + 1 >= _slots.Count) return false;
            double upperPx = _slots[boundaryIndex].Cell.Bounds.Height;
            double lowerPx = _slots[boundaryIndex + 1].Cell.Bounds.Height;
            if (upperPx + lowerPx <= 0) return false;   // 还没排版
            _dragBoundary = boundaryIndex;
            _dragUpperPx = upperPx;
            _dragLowerPx = lowerPx;
            _dragUpperRatio = _slots[boundaryIndex].Ratio;
            _dragLowerRatio = _slots[boundaryIndex + 1].Ratio;
            _dragChanged = false;
            return true;
        }

        /// <param name="deltaY">相对拖拽起点的纵向位移(DIP),向下为正。</param>
        internal void UpdateSplitterDrag(double deltaY)
        {
            if (_dragBoundary < 0 || double.IsNaN(deltaY)) return;
            double pairPx = _dragUpperPx + _dragLowerPx;
            double pairRatio = _dragUpperRatio + _dragLowerRatio;
            // 两块加起来都不够两个最小高度时,最多压到对半,避免上下反转
            double min = Math.Min(_minPaneHeight, pairPx / 2);
            double upperPx = Math.Min(Math.Max(_dragUpperPx + deltaY, min), pairPx - min);
            // 比例只在这两块之间重分,总量不变 → 其它 pane 的像素高度不动
            double upperRatio = pairRatio * upperPx / pairPx;
            var upper = _slots[_dragBoundary];
            var lower = _slots[_dragBoundary + 1];
            if (Math.Abs(upper.Ratio - upperRatio) < 1e-9) return;
            upper.Ratio = upperRatio;
            lower.Ratio = pairRatio - upperRatio;
            _dragChanged = true;
            ApplyRatios();
        }

        internal void EndSplitterDrag()
        {
            if (_dragBoundary < 0) return;
            _dragBoundary = -1;
            if (_dragChanged) PaneRatiosChanged?.Invoke(this, EventArgs.Empty);
        }

        // pane 组成变了时旧快照的下标和像素作废;已经拖出的比例保留
        private void CancelSplitterDrag()
        {
            for (int i = 0; i < _splitters.Count; i++) _splitters[i].CancelDrag();
            EndSplitterDrag();
        }

        // ─── 内部实现 ──────────────────────────────────────────────────────

        private AvaloniaLinkedChartDashboard AppendSlot(ReactiveSchema schema, double heightRatio, SchemaContext? attached)
        {
            // 先注入上下文、挂 BoardActivated,再赋 Template:装配(ComposeAll → InitializeRegistry)从 cell 读上下文,
            // 主数据流跑通后回调 BoardActivated → AttachBoard 把镜像桥订上(顺序同 WPF 版)
            var cell = new AvaloniaChartCell();
            if (attached != null)
            {
                SchemaContext.SetAttached(cell, attached);
                schema.BoardActivated += Context.AttachBoard;
            }
            cell.Template = schema;

            CancelSplitterDrag();
            _slots.Add(new PaneSlot(schema, cell, heightRatio));
            _grid.Children.Add(cell);
            RebuildRows();
            return this;
        }

        // 分隔条打开:pane0, splitter0, pane1, ... —— pane 用星号行,分隔条用 Auto 行;关闭时只有 pane 行。
        // cell 只改 Grid.Row 不移出 Children:移出会让 cell 暂停 / 重新装配
        private void RebuildRows()
        {
            int stride = _paneResizeEnabled ? 2 : 1;
            _grid.RowDefinitions.Clear();
            for (int i = 0; i < _slots.Count; i++)
            {
                if (i > 0 && _paneResizeEnabled) _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var slot = _slots[i];
                slot.Row.MinHeight = _paneResizeEnabled ? _minPaneHeight : 0;
                _grid.RowDefinitions.Add(slot.Row);
                Grid.SetRow(slot.Cell, i * stride);
            }

            int needed = _paneResizeEnabled ? Math.Max(0, _slots.Count - 1) : 0;
            while (_splitters.Count > needed)
            {
                var last = _splitters[_splitters.Count - 1];
                _splitters.RemoveAt(_splitters.Count - 1);
                _grid.Children.Remove(last);
            }
            while (_splitters.Count < needed)
            {
                var splitter = new AvaloniaPaneSplitter(this, _splitters.Count);
                _splitters.Add(splitter);
                _grid.Children.Add(splitter);
            }
            for (int i = 0; i < _splitters.Count; i++)
            {
                _splitters[i].BoundaryIndex = i;
                Grid.SetRow(_splitters[i], i * 2 + 1);
            }

            ApplyRatios();
        }

        private void ApplyRatios()
        {
            for (int i = 0; i < _slots.Count; i++)
                _slots[i].Row.Height = new GridLength(_slots[i].Ratio, GridUnitType.Star);
        }

        private void RefreshSplitterLook()
        {
            for (int i = 0; i < _splitters.Count; i++) _splitters[i].ApplyLook();
        }
    }
}
