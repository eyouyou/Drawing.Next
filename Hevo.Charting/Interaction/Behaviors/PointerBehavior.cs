using Hevo.Charting.Core;

namespace Hevo.Charting.Interaction
{
    /// <summary>交给行为链的输入种类。前八种来自平台输入;<see cref="RouteChanged"/> 是路由器通知"区域手势开始 / 结束 / 要不要藏十字光标"。</summary>
    internal enum PointerEventKind { Press, Release, Move, Leave, Wheel, Key, ManipulationStarting, ManipulationDelta, RouteChanged }

    /// <summary>
    /// 一次交给行为链的输入。值类型、按 ref 传:触屏开始时由行为把要开的模式写回 <see cref="Manipulation"/>。
    /// </summary>
    internal struct PointerEvent
    {
        public PointerEventKind Kind;
        public PointerInput Input;
        public int WheelDelta;
        public PointerKey Key;
        public double TranslateX, ScaleX;
        public HevoPoint Origin;
        /// <summary>ManipulationStarting 的结果;<see cref="ManipulationSet"/> 为 false 时平台保持默认。</summary>
        public PointerManipulation Manipulation;
        public bool ManipulationSet;

        public static PointerEvent Of(PointerEventKind kind, in PointerInput input) => new PointerEvent { Kind = kind, Input = input };
    }

    /// <summary>路由器给行为链的上下文:这次输入在前面几站(捕获者、区域)发生了什么。</summary>
    internal readonly struct PointerRoute
    {
        public PointerRoute(bool claimedAbove, bool gestureActive, bool hideCrosshair)
        {
            ClaimedAbove = claimedAbove; GestureActive = gestureActive; HideCrosshair = hideCrosshair;
        }

        /// <summary>这次按下已被区域认领:行为只做"任何按下都要做的事"(停惯性、武装 hover),不平移 / 框选 / 复原。</summary>
        public bool ClaimedAbove { get; }

        /// <summary>有区域手势进行中:触屏不动、鼠标离开不清十字光标。</summary>
        public bool GestureActive { get; }

        /// <summary>进行中的区域手势要求隐藏十字光标。</summary>
        public bool HideCrosshair { get; }
    }

    /// <summary>
    /// 行为链上的一个行为(平移、框选、滚轮缩放……)。一个行为只管一件事;链按顺序问,返回 true = 这次输入到此为止。
    /// 同 SciChart 的 ChartModifier、OpenLayers 的 Interaction:顺序归框架管,不靠平台事件处理器的先后。
    /// </summary>
    internal interface IPointerBehavior
    {
        bool OnInput(ref PointerEvent e, in PointerRoute route);
    }
}
