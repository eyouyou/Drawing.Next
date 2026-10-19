using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;

namespace Hevo.Charting.Interaction
{
    /// <summary>路由器接收区域表变化与 Feature 卸载的通知。</summary>
    internal interface IPointerRegionListener
    {
        /// <summary>区域表随一帧提交后调用(只在有变化时)。</summary>
        void OnRegionsCommitted();

        /// <summary>Feature 开始卸载、它的 Watch 还活着时调用;此后它的区域被清掉。</summary>
        void OnOwnerRemoving(Feature owner);

        /// <summary>图表 Shutdown,拖动中的手势要取消。</summary>
        void OnUnloading();
    }

    /// <summary>
    /// 一个 ChartCell 上所有 Feature 发布的指针区域。
    /// <para>
    /// 语义同 Trait:<c>OnProject</c> 里写草稿,跟图层 Trait 在同一帧尾一起提交(<see cref="ChartCell.ExecutePipeline"/>
    /// 在 <c>SubmitSync</c> 之后调 <see cref="Commit"/>),所以命中用的位置就是屏幕上那一帧画的位置。
    /// 某个 Feature 这一帧没发布,它上次的区域保持不变;同一帧里多次 Publish 依次追加,下一帧第一次 Publish 换掉整张表。
    /// </para>
    /// <para>
    /// 只在 UI 线程上碰(帧和输入都在 UI 线程),不加锁。提交时把各 Feature 的表拍平成一个数组并算好外包矩形,
    /// 命中只扫这个数组;数组和每个 Feature 的两张表都跨帧复用,稳态零分配。
    /// </para>
    /// </summary>
    internal sealed class PointerRegionStore
    {
        private sealed class OwnerEntry
        {
            public readonly Feature Owner;
            public List<PointerRegion> Live = new();
            public List<PointerRegion> Draft = new();
            public bool HasDraft;
            public int DraftFrame;
            public OwnerEntry(Feature owner) => Owner = owner;
        }

        internal struct FlatRegion
        {
            public PointerRegion Region;
            public Feature Owner;
            public bool HasBounds;
            public float Left, Top, Right, Bottom;
            public int Layer;       // 所属 Feature 图层的最高 ChartLayerType;没有图层 = -1
            public int OwnerOrder;  // 装配顺序(已按 Phase 排过)
            public int Index;       // 在本 Feature 表里的位置,后发布的大
        }

        private readonly Dictionary<Feature, OwnerEntry> _owners = new();
        private readonly List<OwnerEntry> _entries = new();
        private FlatRegion[] _flat = Array.Empty<FlatRegion>();
        private int _flatCount;
        private bool _hasDraft;

        /// <summary>当前 schema 的路由器端口;没挂路由器(非图表 schema)时为 null。</summary>
        internal PointerPorts? Ports { get; set; }

        internal IPointerRegionListener? Listener { get; set; }

        /// <summary>行为链:没被区域拿走的输入按顺序交给它(ChartInteractionFeature 装配时往里装内置交互)。随 cell 存在,跟区域表一样按 Feature 摘除。</summary>
        internal PointerBehaviorChain Behaviors { get; } = new();

        /// <summary>宿主能力(WPF 适配器登记),行为抓鼠标、要帧时钟用。</summary>
        internal IPointerHost? Host { get; set; }

        /// <summary>当前生效的区域数。</summary>
        internal int Count => _flatCount;

        /// <summary>曾经有 Feature 发布过区域(包括已经 Clear 的)。没发布过的图表按下时不付"先跑排队帧"的代价。</summary>
        internal bool EverPublished { get; private set; }

        internal ref readonly FlatRegion this[int index] => ref _flat[index];

        // ==========================================
        // 发布(OnProject 帧路径)
        // ==========================================

        internal void Publish(Feature owner, int frame, PointerRegion region)
        {
            if (region == null) throw new ArgumentNullException(nameof(region));
            Begin(owner, frame).Draft.Add(region);
        }

        internal void Publish(Feature owner, int frame, IReadOnlyList<PointerRegion> regions)
        {
            if (regions == null) throw new ArgumentNullException(nameof(regions));
            var draft = Begin(owner, frame).Draft;
            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i] ?? throw new ArgumentException($"regions[{i}] 是 null。", nameof(regions));
                draft.Add(r);
            }
        }

        internal void Clear(Feature owner, int frame) => Begin(owner, frame);

        // 本帧第一次发布:清空草稿,换掉整张表;同一帧再发布就往草稿里追加
        private OwnerEntry Begin(Feature owner, int frame)
        {
            if (!_owners.TryGetValue(owner, out var e))
            {
                e = new OwnerEntry(owner);
                _owners[owner] = e;
                _entries.Add(e);
            }
            if (!e.HasDraft || e.DraftFrame != frame)
            {
                e.Draft.Clear();
                e.HasDraft = true;
                e.DraftFrame = frame;
            }
            _hasDraft = true;
            EverPublished = true;
            return e;
        }

        // ==========================================
        // 提交(帧尾)
        // ==========================================

        /// <summary>把本帧草稿提交为当前区域表。没有草稿、没有卸载时什么都不做。</summary>
        internal void Commit()
        {
            if (!_hasDraft) return;
            _hasDraft = false;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!e.HasDraft) continue;
                (e.Live, e.Draft) = (e.Draft, e.Live);
                e.Draft.Clear();
                e.HasDraft = false;
            }
            Rebuild();
            Listener?.OnRegionsCommitted();
        }

        /// <summary>
        /// Feature 卸载开头调用(<see cref="Feature.Decompose"/>):先通知路由器(拖动中的手势发 Cancel,此时它的 Watch 还活着),
        /// 再清掉它的区域。
        /// </summary>
        internal void RemoveOwner(Feature owner)
        {
            Listener?.OnOwnerRemoving(owner);
            Behaviors.Remove(owner);
            if (!_owners.TryGetValue(owner, out var e)) return;
            _owners.Remove(owner);
            _entries.Remove(e);
            Rebuild();
            Listener?.OnRegionsCommitted();
        }

        internal void NotifyUnloading() => Listener?.OnUnloading();

        private void Rebuild()
        {
            int total = 0;
            for (int i = 0; i < _entries.Count; i++) total += _entries[i].Live.Count;
            if (_flat.Length < total) _flat = new FlatRegion[Math.Max(total, _flat.Length * 2)];

            int n = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var live = e.Live;
                if (live.Count == 0) continue;
                int layer = e.Owner.TopLayerLevel;
                int order = e.Owner.AssemblyOrder;
                for (int k = 0; k < live.Count; k++)
                {
                    var r = live[k];
                    ref var f = ref _flat[n++];
                    f.Region = r;
                    f.Owner = e.Owner;
                    f.Layer = layer;
                    f.OwnerOrder = order;
                    f.Index = k;
                    f.HasBounds = r.Shape.TryGetBounds(r.Tolerance, out f.Left, out f.Top, out f.Right, out f.Bottom);
                }
            }
            // 尾部旧引用清掉,不让已撤掉的区域 / Feature 跨帧挂在数组里
            for (int i = n; i < _flatCount; i++) _flat[i] = default;
            _flatCount = n;
        }

        // ==========================================
        // 命中
        // ==========================================

        /// <summary>
        /// 按下命中:筛(形状、按键、修饰键、Accepts)→ 优先级高 → 图层靠上 → 离指针近 → 装配靠后 → 表内靠后。
        /// 返回区域下标,没有候选返回 -1。
        /// </summary>
        internal int HitTestPress(HevoPoint p, PointerButton button, PointerModifiers modifiers, HevoRect plotArea, out int hitId)
            => HitTest(p, button, modifiers, plotArea, hover: false, out hitId);

        /// <summary>悬停命中:只看形状和修饰键(悬停时还不知道会按哪个键)。</summary>
        internal int HitTestHover(HevoPoint p, PointerModifiers modifiers, HevoRect plotArea, out int hitId)
            => HitTest(p, PointerButton.None, modifiers, plotArea, hover: true, out hitId);

        private int HitTest(HevoPoint p, PointerButton button, PointerModifiers modifiers, HevoRect plotArea, bool hover, out int hitId)
        {
            int best = -1;
            float bestDist = 0;
            hitId = 0;
            for (int i = 0; i < _flatCount; i++)
            {
                ref readonly var f = ref _flat[i];
                if (f.HasBounds && (p.X < f.Left || p.X > f.Right || p.Y < f.Top || p.Y > f.Bottom)) continue;
                var r = f.Region;
                if (r.Accepts == PointerAccepts.None) continue;
                if ((modifiers & r.RequiredModifiers) != r.RequiredModifiers) continue;
                if (!hover && !AcceptsButton(r, button)) continue;

                int id = r.Id;
                float d = r.Shape.IsCustom ? r.Shape.Tester!.HitTest(p, out id) : r.Shape.Distance(p.X, p.Y, r.Tolerance, plotArea);
                if (float.IsNaN(d)) continue;
                if (best < 0 || Beats(in f, d, in _flat[best], bestDist)) { best = i; bestDist = d; hitId = id; }
            }
            return best;
        }

        internal static bool AcceptsButton(PointerRegion r, PointerButton button)
        {
            const PointerAccepts primary = PointerAccepts.Click | PointerAccepts.Drag | PointerAccepts.DoubleClick;
            if (button == PointerButton.Right && (r.Accepts & PointerAccepts.ContextClick) != 0) return true;
            return r.Button == button && (r.Accepts & primary) != 0;
        }

        private static bool Beats(in FlatRegion a, float da, in FlatRegion b, float db)
        {
            if (a.Region.Priority != b.Region.Priority) return a.Region.Priority > b.Region.Priority;
            if (a.Layer != b.Layer) return a.Layer > b.Layer;
            if (da != db) return da < db;
            if (a.OwnerOrder != b.OwnerOrder) return a.OwnerOrder > b.OwnerOrder;
            return a.Index > b.Index;
        }

        /// <summary>优先级最高的模态区域(Esc 发给它);没有返回 -1。</summary>
        internal int FindModal()
        {
            int best = -1;
            for (int i = 0; i < _flatCount; i++)
            {
                ref readonly var f = ref _flat[i];
                if (!f.Region.Modal) continue;
                if (best < 0 || Beats(in f, 0, in _flat[best], 0)) best = i;
            }
            return best;
        }

        /// <summary>按 (Feature, Id, Part) 找当前表里的区域(自定义形状不看 Id,编号是命中时现算的);找不到返回 -1。</summary>
        internal int Find(Feature owner, int id, int part)
        {
            for (int i = 0; i < _flatCount; i++)
            {
                ref readonly var f = ref _flat[i];
                if (ReferenceEquals(f.Owner, owner) && f.Region.Part == part && (f.Region.Shape.IsCustom || f.Region.Id == id)) return i;
            }
            return -1;
        }
    }
}
