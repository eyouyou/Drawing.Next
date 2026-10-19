using Hevo.Charting.LowCode;

namespace Hevo.Charting.Interaction
{
    /// <summary>区域接受哪些输入。只有接受这次输入的区域才进候选,其余按没命中处理(交给平移 / 框选 / 双击复原)。</summary>
    [Flags]
    public enum PointerAccepts
    {
        None = 0,
        /// <summary>按下后没移出拖动阈值就松开。</summary>
        Click = 1,
        /// <summary>按下后移出拖动阈值。按下时框架即捕获鼠标。</summary>
        Drag = 2,
        /// <summary>同一区域上快速点两次,第二次松开时发 <see cref="PointerPhase.DoubleClick"/>。</summary>
        DoubleClick = 4,
        /// <summary>右键点击,松开时发 <see cref="PointerPhase.ContextClick"/>(弹菜单)。</summary>
        ContextClick = 8,
    }

    /// <summary>区域优先级档位。数值大的先赢;同档再比图层、距离。业务可以取档位之间的值。</summary>
    public static class PointerPriority
    {
        /// <summary>普通对象(价格线、画好的线身)。</summary>
        public const int Object = 100;
        /// <summary>手柄(端点、控制点),压过所属对象。</summary>
        public const int Handle = 200;
        /// <summary>模态(选中画线工具后整块绘图区归它)。</summary>
        public const int Modal = 1000;
    }

    /// <summary>手势进行到哪一步。路由器每走一步往 <see cref="PointerPorts.GesturePort"/> 写一次。</summary>
    public enum PointerPhase
    {
        /// <summary>端口初值,没有任何手势。</summary>
        None = 0,
        /// <summary>按下命中本区域;后面一定跟着 Click / DoubleClick / ContextClick / Begin…End / Cancel 之一收尾。</summary>
        Press,
        /// <summary>移出拖动阈值,拖动开始。</summary>
        Begin,
        Move,
        End,
        Cancel,
        Click,
        DoubleClick,
        ContextClick,
        HoverEnter,
        HoverMove,
        HoverLeave,
        /// <summary>没有手势进行中时按 Esc,发给已发布的模态区域(画线工具退出创建模式)。</summary>
        Escape,
    }

    /// <summary><see cref="PointerPhase.Cancel"/> 的原因。收到 Cancel 的一方应回滚到按下前。</summary>
    public enum PointerCancelReason
    {
        None = 0,
        Escape,
        /// <summary>鼠标捕获被系统或别的控件拿走(切窗口、弹框)。</summary>
        CaptureLost,
        /// <summary>区域所属 Feature 被 Transact 拔掉。此时它的 Watch 仍然有效,可以回滚。</summary>
        OwnerRemoved,
        /// <summary>图表暂停(切走 Tab、最小化)或 Shutdown。</summary>
        ChartUnloaded,
        /// <summary>只接受点击的区域被拖出阈值,改由图表平移接手。</summary>
        SupersededByPan,
        /// <summary>手势中按了另一个鼠标键。</summary>
        OtherButton,
        /// <summary>业务经 <see cref="PointerPorts.RequestCancel"/> 主动中止(拖价格线时委托单被成交)。</summary>
        Requested,
    }

    /// <summary>鼠标按键,取值对齐 Avalonia <c>MouseButton</c>。</summary>
    public enum PointerButton
    {
        None = 0,
        Left,
        Right,
        Middle,
        XButton1,
        XButton2,
    }

    /// <summary>修饰键,取值对齐 Avalonia <c>KeyModifiers</c>。</summary>
    [Flags]
    public enum PointerModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Meta = 8,
    }

    /// <summary>悬停 / 拖动时的光标。<see cref="Default"/> = 不改(交还宿主默认光标)。映射到平台光标只在 WPF 适配器里。</summary>
    public enum PointerCursor
    {
        Default = 0,
        Arrow,
        Hand,
        Cross,
        /// <summary>上下箭头(拖水平线)。</summary>
        SizeNS,
        /// <summary>左右箭头(拖竖线)。</summary>
        SizeWE,
        SizeAll,
        SizeNWSE,
        SizeNESW,
        IBeam,
        No,
    }

    /// <summary>
    /// 一块能点 / 能拖的区域。在 <c>OnProject</c> 里用 <c>ctx.Pointer().Publish(...)</c> 发布,语义同 Trait:
    /// 跟图层 Trait 同一帧提交,命中用的位置 = 屏幕上那一帧画的位置。
    /// 能不能点只看发不发布:暂时不可点(画线已锁定)就不发布它。
    /// </summary>
    public sealed record PointerRegion
    {
        /// <summary>业务自己的编号(第几条线),事件里原样带回。</summary>
        public int Id { get; init; }

        /// <summary>部位:如线身 0 / 起点手柄 1 / 终点手柄 2。事件里原样带回。</summary>
        public int Part { get; init; }

        /// <summary>哪里(DrawingCanvas 坐标,DIP)。</summary>
        public PointerShape Shape { get; init; }

        public PointerAccepts Accepts { get; init; } = PointerAccepts.Drag;

        /// <summary>悬停光标。</summary>
        public PointerCursor Cursor { get; init; }

        /// <summary>拖动中的光标;<see cref="PointerCursor.Default"/> = 沿用 <see cref="Cursor"/>。</summary>
        public PointerCursor DragCursor { get; init; }

        /// <summary>线类形状的命中容差(DIP)。面类形状(矩形、圆、整块绘图区)按包含,不看它。</summary>
        public float Tolerance { get; init; } = 4;

        /// <summary>见 <see cref="PointerPriority"/>。</summary>
        public int Priority { get; init; } = PointerPriority.Object;

        /// <summary>点击 / 拖动用哪个键。右键菜单另看 <see cref="PointerAccepts.ContextClick"/>,不受它限制。</summary>
        public PointerButton Button { get; init; } = PointerButton.Left;

        /// <summary>必须按着的修饰键;没按全时不进候选(按下照常平移,悬停不改光标)。</summary>
        public PointerModifiers RequiredModifiers { get; init; }

        /// <summary>模态区域:没有手势进行中时按 Esc,发 <see cref="PointerPhase.Escape"/> 给它。</summary>
        public bool Modal { get; init; }

        /// <summary>
        /// 框架内部:只接受双击的区域"旁观"第二次按下 —— 发 DoubleClick 但不认领,按下照常交给兜底(双击复原照常触发)。
        /// 判定用平台的连击计数(WPF ClickCount),跟原来 MouseLeftButtonDown + ClickCount == 2 的写法一致。
        /// </summary>
        internal bool PassThrough { get; init; }

        /// <summary>拖动中隐藏十字光标。</summary>
        public bool HideCrosshairWhileActive { get; init; }
    }

    /// <summary>
    /// 区域形状。封闭集合、纯数据:命中测试在框架里做,不进业务代码(<c>OnProject</c> 里不能 new 闭包)。
    /// 复杂形状(斐波那契)拆成多个区域,共用 <see cref="PointerRegion.Id"/>。
    /// </summary>
    public readonly struct PointerShape
    {
        internal enum ShapeKind : byte { None, HLine, VLine, Segment, Rect, Circle, Polyline, WholePlot, Custom }

        internal readonly ShapeKind Kind;
        internal readonly float A, B, C, D;   // HLine: y,left,right; VLine: x,top,bottom; Segment: ax,ay,bx,by; Rect: x,y,w,h; Circle: cx,cy,r
        internal readonly ReadOnlyMemory<HevoPoint> Points;
        internal readonly IPointerHitTester? Tester;

        private PointerShape(ShapeKind kind, float a, float b, float c, float d, ReadOnlyMemory<HevoPoint> points = default, IPointerHitTester? tester = null)
        {
            Kind = kind; A = a; B = b; C = c; D = d; Points = points; Tester = tester;
        }

        /// <summary>
        /// 框架内部:命中时现算(ElementPlot 按命中那一刻的投影找最近元素,跟原来 MouseMove 里现算一致)。
        /// 命中结果带回元素编号,作为手势的 RegionId。业务用不到(封闭形状集合,见 Q7)。
        /// </summary>
        internal static PointerShape Custom(IPointerHitTester tester) => new PointerShape(ShapeKind.Custom, 0, 0, 0, 0, default, tester);

        internal bool IsCustom => Kind == ShapeKind.Custom;

        public bool IsEmpty => Kind == ShapeKind.None;

        public static PointerShape HLine(float y, float left, float right)
            => new PointerShape(ShapeKind.HLine, y, Math.Min(left, right), Math.Max(left, right), 0);

        public static PointerShape VLine(float x, float top, float bottom)
            => new PointerShape(ShapeKind.VLine, x, Math.Min(top, bottom), Math.Max(top, bottom), 0);

        public static PointerShape Segment(HevoPoint a, HevoPoint b)
            => new PointerShape(ShapeKind.Segment, a.X, a.Y, b.X, b.Y);

        public static PointerShape Rect(HevoRect r)
            => new PointerShape(ShapeKind.Rect, r.X, r.Y, r.Width, r.Height);

        public static PointerShape Circle(HevoPoint c, float radius)
            => new PointerShape(ShapeKind.Circle, c.X, c.Y, Math.Abs(radius), 0);

        /// <summary>折线,按到各段的最近距离命中。数组由业务持有,发布后不要原地改(同列端口的规矩)。</summary>
        public static PointerShape Polyline(ReadOnlyMemory<HevoPoint> points)
            => new PointerShape(ShapeKind.Polyline, 0, 0, 0, 0, points);

        /// <summary>整块绘图区。按命中时的绘图区算,画布尺寸变了不用重发。</summary>
        public static PointerShape WholePlot() => new PointerShape(ShapeKind.WholePlot, 0, 0, 0, 0);

        internal bool IsArea => Kind == ShapeKind.Rect || Kind == ShapeKind.Circle || Kind == ShapeKind.WholePlot;

        /// <summary>外包矩形(线类已按容差放大)。WholePlot 返回 false,命中时另按绘图区判断。</summary>
        internal bool TryGetBounds(float tolerance, out float left, out float top, out float right, out float bottom)
        {
            float t = IsArea ? 0 : Math.Max(0, tolerance);
            switch (Kind)
            {
                case ShapeKind.HLine: left = B - t; right = C + t; top = A - t; bottom = A + t; return true;
                case ShapeKind.VLine: left = A - t; right = A + t; top = B - t; bottom = C + t; return true;
                case ShapeKind.Segment:
                    left = Math.Min(A, C) - t; right = Math.Max(A, C) + t;
                    top = Math.Min(B, D) - t; bottom = Math.Max(B, D) + t; return true;
                case ShapeKind.Rect: left = A; top = B; right = A + C; bottom = B + D; return true;
                case ShapeKind.Circle: left = A - C; right = A + C; top = B - C; bottom = B + C; return true;
                case ShapeKind.Polyline:
                {
                    var span = Points.Span;
                    if (span.Length == 0) break;
                    float l = span[0].X, r = l, tp = span[0].Y, bt = tp;
                    for (int i = 1; i < span.Length; i++)
                    {
                        var p = span[i];
                        if (p.X < l) l = p.X; else if (p.X > r) r = p.X;
                        if (p.Y < tp) tp = p.Y; else if (p.Y > bt) bt = p.Y;
                    }
                    left = l - t; right = r + t; top = tp - t; bottom = bt + t; return true;
                }
            }
            left = top = right = bottom = 0;
            return false;
        }

        /// <summary>
        /// 命中时返回到形状的距离(面类在里面 = 0),没命中返回 <see cref="float.NaN"/>。
        /// 线类按距离 ≤ 容差,面类按包含。
        /// </summary>
        internal float Distance(float x, float y, float tolerance, HevoRect plotArea)
        {
            float t = Math.Max(0, tolerance);
            switch (Kind)
            {
                case ShapeKind.HLine:
                {
                    if (x < B - t || x > C + t) return float.NaN;
                    float d = Math.Abs(y - A);
                    return d <= t ? d : float.NaN;
                }
                case ShapeKind.VLine:
                {
                    if (y < B - t || y > C + t) return float.NaN;
                    float d = Math.Abs(x - A);
                    return d <= t ? d : float.NaN;
                }
                case ShapeKind.Segment:
                {
                    float d = SegmentDistance(x, y, A, B, C, D);
                    return d <= t ? d : float.NaN;
                }
                case ShapeKind.Rect:
                    return x >= A && x <= A + C && y >= B && y <= B + D ? 0f : float.NaN;
                case ShapeKind.Circle:
                {
                    float dx = x - A, dy = y - B;
                    return dx * dx + dy * dy <= C * C ? 0f : float.NaN;
                }
                case ShapeKind.Custom:
                    return Tester!.HitTest(new HevoPoint(x, y), out _);
                case ShapeKind.WholePlot:
                    return !plotArea.IsEmpty && x >= plotArea.Left && x <= plotArea.Right && y >= plotArea.Top && y <= plotArea.Bottom
                        ? 0f : float.NaN;
                case ShapeKind.Polyline:
                {
                    var span = Points.Span;
                    if (span.Length == 0) return float.NaN;
                    float best = float.MaxValue;
                    if (span.Length == 1) best = SegmentDistance(x, y, span[0].X, span[0].Y, span[0].X, span[0].Y);
                    for (int i = 1; i < span.Length; i++)
                    {
                        float d = SegmentDistance(x, y, span[i - 1].X, span[i - 1].Y, span[i].X, span[i].Y);
                        if (d < best) best = d;
                    }
                    return best <= t ? best : float.NaN;
                }
            }
            return float.NaN;
        }

        private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float len2 = vx * vx + vy * vy;
            float u = len2 > 0 ? ((px - ax) * vx + (py - ay) * vy) / len2 : 0;
            if (u < 0) u = 0; else if (u > 1) u = 1;
            float dx = px - (ax + u * vx), dy = py - (ay + u * vy);
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>框架内部的现算命中:命中返回距离(并给出编号),没命中返回 NaN。</summary>
    internal interface IPointerHitTester
    {
        float HitTest(HevoPoint p, out int id);
    }

    /// <summary>
    /// 手势端口上的一条记录。每次鼠标移动都要写,所以是值类型;业务只读不构造。
    /// 坐标都是 DrawingCanvas 坐标(DIP),跟十字光标命中同一口径。
    /// </summary>
    public readonly struct PointerGesture : IEquatable<PointerGesture>
    {
        internal PointerGesture(long sequence, object? owner, int regionId, int regionPart, PointerPhase phase, PointerCancelReason reason,
            HevoPoint position, HevoPoint pressPosition, PointerButton button, PointerModifiers modifiers)
        {
            Sequence = sequence; Owner = owner; RegionId = regionId; RegionPart = regionPart; Phase = phase; Reason = reason;
            Position = position; PressPosition = pressPosition; Button = button; Modifiers = modifiers;
        }

        /// <summary>每次写入递增:连续两次一模一样的点击也是两条不同的记录。</summary>
        public long Sequence { get; }

        /// <summary>区域所属 Feature。</summary>
        public object? Owner { get; }
        public int RegionId { get; }
        public int RegionPart { get; }
        public PointerPhase Phase { get; }

        /// <summary>只在 <see cref="PointerPhase.Cancel"/> 时有值。</summary>
        public PointerCancelReason Reason { get; }

        public HevoPoint Position { get; }

        /// <summary>这次按下的位置;悬停 / Escape 时等于 <see cref="Position"/>。</summary>
        public HevoPoint PressPosition { get; }
        public PointerButton Button { get; }

        /// <summary>写入这一刻按着的修饰键(拖动中按 Shift 也能实时读到)。</summary>
        public PointerModifiers Modifiers { get; }

        public bool IsFor(object feature) => Owner != null && ReferenceEquals(Owner, feature);

        // 每条记录的 Sequence 唯一:按它比就够,也让端口的 WriteIfChanged 走 IEquatable 不装箱
        public bool Equals(PointerGesture other) => Sequence == other.Sequence && ReferenceEquals(Owner, other.Owner);
        public override bool Equals(object? obj) => obj is PointerGesture other && Equals(other);
        public override int GetHashCode() => Sequence.GetHashCode();

        public override string ToString() => $"#{Sequence} {Phase}{(Reason != PointerCancelReason.None ? "(" + Reason + ")" : "")} id={RegionId}.{RegionPart} @({Position.X:F0},{Position.Y:F0})";
    }

    /// <summary>
    /// 路由器的端口。每个 schema 一份,由框架注入到每个 <see cref="Core.ChartFeature.Pointer"/>,
    /// 跟 <see cref="Core.ChartFeature.Viewport"/> 一样直接用。不进联动镜像:副图收不到主图的手势。
    /// </summary>
    public sealed class PointerPorts
    {
        /// <summary>只由路由器写。Watch 它,再用 <see cref="PointerGesture.IsFor"/> 挑出自己的。</summary>
        [PortDirection(PortDirection.Output)]
        public DataPort<PointerGesture> GesturePort { get; } = new("Pointer_Gesture");

        /// <summary>"请求取消"计数:每次加一,路由器就以 <see cref="PointerCancelReason.Requested"/> 取消进行中的手势。用 <see cref="RequestCancel"/> 写。</summary>
        public DataPort<long> CancelRequestPort { get; } = new("Pointer_CancelRequest");

        /// <summary>
        /// 主动中止进行中的手势(没有就什么都不做)。须在写锁内调用(手势回调里直接调;别处套 <c>AcquireWriteLock</c>)。
        /// 在手势回调里调用时,等这次分发结束才生效。
        /// </summary>
        public void RequestCancel(DataBlackboard board)
        {
            if (board == null) throw new ArgumentNullException(nameof(board));
            board.WriteIfChanged(CancelRequestPort, board.Read(CancelRequestPort) + 1);
        }
    }
}
