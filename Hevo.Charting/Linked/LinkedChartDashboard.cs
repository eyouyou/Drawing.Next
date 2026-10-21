using Hevo.Charting.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hevo.Charting.Linked
{
    /// <summary>
    /// 列式联动 dashboard 容器(领域无关):主图 + N 副图垂直堆叠,共享视口与 hit。
    ///
    /// <para>
    /// schema 永远写成独立模式(不知道 dashboard / 联动概念存在);本容器通过
    /// <see cref="SchemaContext"/> 的 attached property 把"主图 / 副图"上下文注入到对应 cell:
    /// <list type="bullet">
    ///   <item><b>主图</b>(<see cref="AddMaster"/>):<see cref="SchemaContext.LinkedMaster"/></item>
    ///   <item><b>副图</b>(<see cref="AddPane"/>):<see cref="SchemaContext.LinkedPane"/>(摘视口管家 + tooltip)</item>
    ///   <item><b>纯堆叠</b>(<see cref="AddRaw"/>):不注入,完全不联动</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 联动落地靠 <see cref="LinkedChartContext"/> 的端口镜像桥:每个 schema 的主数据流 board
    /// 就绪时通过 <see cref="ReactiveSchema.BoardActivated"/> 事件 attach,几个共享 port
    /// 写入时双向同步到所有 board。
    /// </para>
    ///
    /// <para>
    /// pane 高度:<see cref="IsPaneResizeEnabled"/> 打开后(默认关,保持旧布局)相邻 pane 之间有 <see cref="PaneSplitter"/>,拖动只改两侧 pane,其余不动;
    /// 高度以比例保存(<see cref="GetPaneRatios"/> / <see cref="TrySetPaneRatios"/>),窗口缩放时按比例重排;
    /// 双击分隔条或 <see cref="ResetPaneRatios"/> 回到装配时给的 heightRatio。
    /// 宿主要记住用户布局就在 <see cref="PaneRatiosChanged"/> 里存 <see cref="GetPaneRatios"/>,下次装配完调 <see cref="TrySetPaneRatios"/>。
    /// </para>
    /// </summary>
    public class LinkedChartDashboard : UserControl, LowCode.Designer.GraphViewer.ILinkedChartDashboard
    {
        public LinkedChartContext Context { get; }

        private readonly Grid _grid = new();
        private readonly List<PaneSlot> _slots = new();
        private readonly List<PaneSplitter> _splitters = new();
        private sealed class PaneSlot
        {
            public PaneSlot(ReactiveSchema schema, ChartCell cell, double defaultRatio)
            {
                Schema = schema;
                Cell = cell;
                DefaultRatio = defaultRatio;
                Ratio = defaultRatio;
            }

            public ReactiveSchema Schema { get; }
            public ChartCell Cell { get; }
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
        private Brush _splitterBrush = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        private Brush _splitterHoverBrush = Frozen(Color.FromArgb(0xC0, 0x80, 0x80, 0x80));

        /// <summary>
        /// 当前所有 cell 的只读快照,顺序 = AddMaster / AddPane / AddRaw 加入顺序。
        /// <para>给 DashboardLauncher 这种外部装配器拿出 cell 引用做后置 wiring 用(典型:per-cell <c>IsLoading</c> 翻牌)。</para>
        /// </summary>
        public IReadOnlyList<ChartCell> Cells
        {
            get
            {
                var list = new List<ChartCell>(_slots.Count);
                for (int i = 0; i < _slots.Count; i++) list.Add(_slots[i].Cell);
                return list;
            }
        }

        /// <summary>当前分隔条快照,第 i 个位于第 i 和 i+1 个 pane 之间。</summary>
        public IReadOnlyList<PaneSplitter> Splitters => _splitters.ToArray();

        /// <summary>
        /// 是否在 pane 之间放可拖拽的分隔条。<b>默认关</b>:已有 dashboard 的行布局、pane 高度、可视树保持原样
        /// (只有 pane 行,没有分隔条行,行无 MinHeight)。打开后分隔条各占 <see cref="SplitterThickness"/> 高度,
        /// pane 行加 <see cref="MinPaneHeight"/>。可随时切换;比例(<see cref="GetPaneRatios"/> 等)开关与否都可用。
        /// </summary>
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

        /// <summary>
        /// 拖拽时单个 pane 至少保留的高度(DIP)。分隔条打开时也作为行的 MinHeight,窗口缩得很小时 pane 不会被压成 0。
        /// 默认 48:再小就只剩坐标轴、看不出图了。
        /// </summary>
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
        public Brush SplitterBrush
        {
            get => _splitterBrush;
            set { _splitterBrush = value ?? throw new ArgumentNullException(nameof(value)); RefreshSplitterLook(); }
        }

        /// <summary>分隔线颜色(悬停 / 拖拽中)。</summary>
        public Brush SplitterHoverBrush
        {
            get => _splitterHoverBrush;
            set { _splitterHoverBrush = value ?? throw new ArgumentNullException(nameof(value)); RefreshSplitterLook(); }
        }

        /// <summary>
        /// pane 比例被用户或 API 改动后触发(拖拽松手、双击复位、<see cref="TrySetPaneRatios"/>、<see cref="ResetPaneRatios"/>)。
        /// 拖拽过程中不触发,宿主可以直接在这里落盘。
        /// </summary>
        public event EventHandler? PaneRatiosChanged;

        public LinkedChartDashboard(LinkedChartContext ctx)
        {
            Context = ctx;
            Content = _grid;
        }

        // ─── 装配期 API ────────────────────────────────────────────────────

        /// <summary>加主图。<b>必须最先且只能一次</b>。主图独占视口管家、tooltip、心跳/分页等。</summary>
        public LinkedChartDashboard AddMaster(ReactiveSchema schema, double heightRatio = 3)
        {
            if (_slots.Count > 0)
                throw new InvalidOperationException("Master 必须最先加,且只能一个");
            return AppendSlot(schema, heightRatio, SchemaContext.LinkedMaster(Context));
        }

        /// <summary>加副图。装饰钩子自动摘除视口管家/tooltip 并挂 ExternalViewportFeature 标记。</summary>
        public LinkedChartDashboard AddPane(ReactiveSchema schema, double heightRatio = 1)
            => AppendSlot(schema, heightRatio, SchemaContext.LinkedPane(Context));

        /// <summary>加 raw schema(<b>不联动</b>,纯堆叠)。该 cell 自己的 viewport / hit / tooltip 完全独立。</summary>
        public LinkedChartDashboard AddRaw(ReactiveSchema schema, double heightRatio = 1)
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

        /// <summary>运行期热删副图。<b>主图(行 0)不允许删</b>,删了等于砍掉视口管家。</summary>
        public bool RemovePaneAtRuntime(ReactiveSchema schema)
        {
            int idx = _slots.FindIndex(s => ReferenceEquals(s.Schema, schema));
            if (idx <= 0) return false;   // idx == 0 是主图,禁止移除

            CancelSplitterDrag();
            _grid.Children.Remove(_slots[idx].Cell);
            _slots.RemoveAt(idx);
            RebuildRows();
            return true;
        }

        // ─── pane 高度 ─────────────────────────────────────────────────────

        /// <summary>
        /// 当前各 pane 的高度比例,顺序同 <see cref="Cells"/>,总和归一为 1。
        /// 存的是比例不是像素,换窗口尺寸 / DPI 恢复后仍是同样的分配。
        /// </summary>
        public double[] GetPaneRatios()
        {
            var result = new double[_slots.Count];
            double sum = 0;
            for (int i = 0; i < _slots.Count; i++) sum += _slots[i].Ratio;
            for (int i = 0; i < _slots.Count; i++) result[i] = sum > 0 ? _slots[i].Ratio / sum : 0;
            return result;
        }

        /// <summary>
        /// 恢复保存过的比例。个数必须等于当前 pane 数,每项为正有限数(不要求归一)。
        /// 不合法(典型:保存时的 pane 组成和现在不同)返回 false 并保持原状,宿主可回落默认布局。
        /// </summary>
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

        // 拖拽三段由 PaneSplitter 驱动;测试直接调(鼠标位置在离屏窗口里没法伪造)

        internal bool BeginSplitterDrag(int boundaryIndex)
        {
            if (boundaryIndex < 0 || boundaryIndex + 1 >= _slots.Count) return false;
            var upper = _slots[boundaryIndex];
            var lower = _slots[boundaryIndex + 1];
            if (upper.Row.ActualHeight + lower.Row.ActualHeight <= 0) return false;   // 还没排版
            _dragBoundary = boundaryIndex;
            _dragUpperPx = upper.Row.ActualHeight;
            _dragLowerPx = lower.Row.ActualHeight;
            _dragUpperRatio = upper.Ratio;
            _dragLowerRatio = lower.Ratio;
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

        // pane 组成变了(删 pane / 改比例)时旧快照的下标和像素作废;已经拖出的比例保留
        private void CancelSplitterDrag()
        {
            for (int i = 0; i < _splitters.Count; i++)
                if (_splitters[i].IsMouseCaptured) _splitters[i].ReleaseMouseCapture();
            EndSplitterDrag();
        }

        // ─── 内部实现 ──────────────────────────────────────────────────────

        private LinkedChartDashboard AppendSlot(ReactiveSchema schema, double heightRatio, SchemaContext? attached)
        {
            // 必须先 SetAttached + 挂 BoardActivated、再赋 Template:Template 触发 OnApplyTemplate →
            // ComposeAll → ReactiveSchema.InitializeRegistry,后者从 cell 读 attached 上下文,
            // 主数据流跑通后回调 BoardActivated → AttachBoard 把镜像桥订上。
            var cell = new ChartCell();
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

        // 分隔条打开:pane0, splitter0, pane1, splitter1, ... —— pane 用星号行(窗口缩放按比例重排),分隔条用 Auto 行。
        // 分隔条关闭:只有 pane 行,和加分隔条之前的布局逐行一致。
        // cell 只改 Grid.Row 不移出 Children:移出会触发 Unloaded,cell 会暂停 / 重新装配。
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
                var splitter = new PaneSplitter(this, _splitters.Count);
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

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
