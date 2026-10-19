using Hevo.Charting.Core;

namespace Hevo.Charting.Interaction
{
    /// <summary>
    /// 一个 ChartCell 上的行为链:没被区域拿走的输入按顺序交给这里的行为。
    /// <para>
    /// 行为按所属 Feature 分组登记(<see cref="Features.ChartInteractionFeature"/> 装配时登记一组),组的先后 = 装配顺序,
    /// 组内先后 = 登记时给的顺序。任何一个行为返回 true,整条链到此为止。所属 Feature 挂起时整组跳过(同原来订阅的暂停闸门);
    /// 卸载时(<see cref="PointerRegionStore.RemoveOwner"/>)整组摘掉。
    /// </para>
    /// 只在 UI 线程上碰,不加锁。
    /// </summary>
    internal sealed class PointerBehaviorChain
    {
        private sealed class Group
        {
            public readonly Feature Owner;
            public readonly IPointerBehavior[] Behaviors;
            public readonly object? State;
            public readonly TimeSpan DoubleClickInterval;
            public readonly double DoubleClickMaxMove;
            public readonly Action? OnRemoved;

            public Group(Feature owner, IPointerBehavior[] behaviors, object? state, TimeSpan interval, double maxMove, Action? onRemoved)
            {
                Owner = owner; Behaviors = behaviors; State = state; DoubleClickInterval = interval; DoubleClickMaxMove = maxMove; OnRemoved = onRemoved;
            }
        }

        private readonly List<Group> _groups = new();

        internal int Count => _groups.Count;

        /// <param name="state">组的共享状态(测试 / 诊断用 <see cref="StateOf{T}"/> 取回)。</param>
        /// <param name="doubleClickInterval">区域双击计数用的窗口,取第一组的。</param>
        /// <param name="onRemoved">整组摘掉时调用:停惯性、放掉自己抓的鼠标。</param>
        internal void Add(Feature owner, IPointerBehavior[] behaviors, object? state, TimeSpan doubleClickInterval, double doubleClickMaxMove, Action? onRemoved)
        {
            Remove(owner);
            _groups.Add(new Group(owner, behaviors, state, doubleClickInterval, doubleClickMaxMove, onRemoved));
        }

        internal void Remove(Feature owner)
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                if (!ReferenceEquals(g.Owner, owner)) continue;
                _groups.RemoveAt(i);
                g.OnRemoved?.Invoke();
                return;
            }
        }

        internal T? StateOf<T>(Feature owner) where T : class
        {
            for (int i = 0; i < _groups.Count; i++)
                if (ReferenceEquals(_groups[i].Owner, owner)) return _groups[i].State as T;
            return null;
        }

        /// <summary>区域双击计数的窗口:第一组的;没有组时 400ms / 6px(同 <see cref="Features.DoubleClickResetOptions"/> 默认)。</summary>
        internal void GetDoubleClickWindow(out TimeSpan interval, out double maxMove)
        {
            if (_groups.Count > 0) { interval = _groups[0].DoubleClickInterval; maxMove = _groups[0].DoubleClickMaxMove; return; }
            interval = TimeSpan.FromMilliseconds(400);
            maxMove = 6.0;
        }

        /// <summary>按顺序交给每个行为,返回 true = 有行为把它拦下了。</summary>
        internal bool Dispatch(ref PointerEvent e, in PointerRoute route)
        {
            // 行为里写黑板可能同步触发别的 Watch,极端情况下摘掉本链上的组:按下标走、每步复查边界
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                if (!g.Owner.IsActive) continue;
#if DEBUG
                // 拓扑面板:行为写的黑板记在所属 Feature 名下(同原来 OnUIEvent 的追踪作用域)
                using var scope = DevTools.TopologyTracer.EnterScope(g.Owner);
#endif
                var behaviors = g.Behaviors;
                for (int k = 0; k < behaviors.Length; k++)
                    if (behaviors[k].OnInput(ref e, route)) return true;
            }
            return false;
        }
    }
}
