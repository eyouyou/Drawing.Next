using Hevo.Charting.Abstractions;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// <see cref="ChartCell.AddUnmanagedLayer{TLayer}"/> 返回的代理背后的写入口:不写 Feature 时给图层喂本层数据。
    /// <para>
    /// 写进来的 Trait 先攒着,排一帧,在下一帧开头并进该图层的事务草稿 —— 跟 Feature 在 OnProject 里
    /// <c>ctx.For(layer).PublishData</c> 走同一条路:SubmitSync 合并草稿时唤醒深度睡眠的图层、
    /// 已唤醒的图层按引用判脏。所以"喂了本层数据才画"的规则对两条路一致。
    /// </para>
    /// </summary>
    // 不直接写 live 袋:直写不经过事务草稿,SubmitSync 看不到"本层有新数据",图层永远不醒(以前的行为);
    // 而且调用方可能在后台线程喂数据,攒在这里加锁,真正并入草稿在 UI 线程的帧开头做。
    internal sealed class LayerFeed : IVisualData
    {
        private readonly ChartEngine _cell;
        private readonly IChartLayer _layer;
        private readonly VisualDataBag _live;
        private readonly VisualDataBag _pending = new();
        private readonly object _gate = new();
        private readonly Action<RenderContext> _flush;
        private bool _flushScheduled;

        public LayerFeed(ChartEngine cell, IChartLayer layer, VisualDataBag live)
        {
            _cell = cell;
            _layer = layer;
            _live = live;
            _flush = Flush;
        }

        public void Publish<T>(T snapshot) where T : class
        {
            bool schedule;
            lock (_gate)
            {
                _pending.Publish(snapshot);
                schedule = !_flushScheduled;
                _flushScheduled = true;
            }
            // 同一帧前喂多次只排一次;RequestUpdate 本身线程安全
            if (schedule) _cell.RequestUpdate(_flush);
        }

        // 还没并进去的值优先:代理上连着 UpdateData 两次,第二次要读到第一次的结果
        public T? Get<T>() where T : class
        {
            lock (_gate) return _pending.Get<T>() ?? _live.Get<T>();
        }

        private void Flush(RenderContext ctx)
        {
            lock (_gate)
            {
                ctx.PublishLocalDraft(_layer, _pending);
                _pending.Clear();
                _flushScheduled = false;
            }
        }
    }
}
