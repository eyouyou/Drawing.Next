namespace Hevo.Charting
{
    /// <summary>
    /// <see cref="Renderers.DrawingExtensions.DrawBand"/> 的数据:上下两条按下标对齐的折线(屏幕坐标)。
    /// <para>
    /// 第 i 个上点和第 i 个下点是同一个样本(通常同一个 x;通道 / 画线工具的上下边 x 可以不同)。
    /// 任一边是 NaN / ∞ 的样本断开带子,两侧各自成段 —— 指标暖机期、停牌缺口直接写 NaN 即可。
    /// </para>
    /// <para>
    /// 跟 <c>DrawPolyline</c> 的点列表一样,命令只引用这个对象不拷贝:做成图层的跨帧字段,
    /// 每帧 <see cref="Clear"/> 后重填(0-GC);一帧里同一个实例只录一次 DrawBand。
    /// </para>
    /// </summary>
    public sealed class HevoBand
    {
        /// <summary>上边(屏幕上方那条)。可直接按下标填,跟 <see cref="Lower"/> 等长。</summary>
        public List<HevoPoint> Upper { get; } = new();

        /// <summary>下边。</summary>
        public List<HevoPoint> Lower { get; } = new();

        public HevoBand() { }

        public HevoBand(int capacity)
        {
            Upper.Capacity = capacity;
            Lower.Capacity = capacity;
        }

        /// <summary>样本数(两边取短的)。</summary>
        public int Count => Math.Min(Upper.Count, Lower.Count);

        public void Clear()
        {
            Upper.Clear();
            Lower.Clear();
        }

        /// <summary>同一个 x 上的一对上下值(区间填色、布林带的常见写法)。y 传 NaN 即断开。</summary>
        public void Add(float x, float upperY, float lowerY)
        {
            Upper.Add(new HevoPoint(x, upperY));
            Lower.Add(new HevoPoint(x, lowerY));
        }

        public void Add(HevoPoint upper, HevoPoint lower)
        {
            Upper.Add(upper);
            Lower.Add(lower);
        }

        /// <summary>在当前位置断开带子。</summary>
        public void AddGap() => Add(float.NaN, float.NaN, float.NaN);

        // DrawBand 录制时写入:上下穿越后(下边跑到上边之上)那部分的填充。DrawCmd 只有一个画刷槽位,
        // 第二个画刷跟着数据走,省掉每条命令一个包装对象;同一实例一帧只录一次,所以不会被覆盖。
        internal IHevoBrush? CrossedBrush;
    }
}
