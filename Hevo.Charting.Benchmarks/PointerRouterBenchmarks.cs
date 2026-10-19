using BenchmarkDotNet.Attributes;
using Hevo.Charting.Core;
using Hevo.Charting.Interaction;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;

namespace Hevo.Charting.Benchmarks
{
    /// <summary>
    /// 指针路由器的两条热路径:
    /// ① 有区域、没在拖:每次鼠标移动做一次悬停命中(300 个区域:200 条水平线 + 60 条线段 + 40 个矩形,同一 Feature);
    /// ② 拖动中:每次移动只写一次手势端口(不做命中),目标零分配。
    /// 不经 WPF:直接驱动 internal 的区域表 / 路由器,宿主是一个空壳。
    /// <c>dotnet run -c Release -- --filter "*PointerRouter*"</c>
    /// </summary>
    [MemoryDiagnoser]
    public class PointerRouterBenchmarks
    {
        private sealed class Owner : Feature
        {
            protected override void OnCompose(ChartCell chart, RenderContext ctx, IRenderFlow<DataBlackboard> flow) { }
            protected override void OnProject(FeatureContext ctx) { }
        }

        private sealed class Host : IPointerHost
        {
            public DataBlackboard? Board { get; set; }
            public bool HasPendingFrame => false;
            public void RunPendingFrame() { }
            public void Focus() { }
            public void Capture() { }
            public void ReleaseCapture() { }
            public void HandOverCapture() { }
            public bool IsMouseCapturedByOther => false;
            public void SetCursor(PointerCursor cursor) { }
            public HevoRect PlotArea => new HevoRect(50, 10, 1200, 700);
            public void CaptureForBackground() { }
            public void ReleaseBackgroundCapture() { }
            public int BackgroundCaptureCount => 0;
            public void AddFrameCallback(Action callback) { }
            public void RemoveFrameCallback(Action callback) { }
            public double DragThreshold => 4;
        }

        private PointerRegionStore _store = null!;
        private PointerRouter _router = null!;
        private Host _host = null!;
        private float _x;

        [GlobalSetup]
        public void Setup()
        {
            _store = new PointerRegionStore();
            var owner = new Owner();
            var regions = new List<PointerRegion>();
            for (int i = 0; i < 200; i++) regions.Add(new PointerRegion { Id = i, Shape = PointerShape.HLine(12 + i * 3.4f, 50, 1250), Cursor = PointerCursor.SizeNS });
            for (int i = 0; i < 60; i++) regions.Add(new PointerRegion { Id = 200 + i, Shape = PointerShape.Segment(new HevoPoint(60 + i * 19, 20), new HevoPoint(80 + i * 19, 690)) });
            for (int i = 0; i < 40; i++) regions.Add(new PointerRegion { Id = 260 + i, Shape = PointerShape.Rect(new HevoRect(60 + i * 29, 300, 12, 12)), Accepts = PointerAccepts.Click });
            _store.Publish(owner, 1, regions);
            _store.Commit();

            var ports = new PointerPorts();
            _router = new PointerRouter(_store, ports);
            _host = new Host { Board = new DataBlackboard() };
            _router.AttachHost(_host);

            // 进入拖动:按在第 0 条线上,移出阈值
            _router.OnPress(new PointerInput(new HevoPoint(600, 12), new HevoPoint(600, 12), PointerButton.Left, PointerModifiers.None, 0));
            _router.OnMove(new PointerInput(new HevoPoint(600, 40), new HevoPoint(600, 40), PointerButton.None, PointerModifiers.None, 1));
        }

        [Benchmark(Description = "悬停命中:300 个区域,单次移动")]
        public int HitTest_300Regions()
        {
            _x = _x > 1200 ? 60 : _x + 7.3f;
            return _store.HitTestHover(new HevoPoint(_x, 351), PointerModifiers.None, _host.PlotArea, out _);
        }

        private readonly DataPort<double> _plain = new("Bench_Plain");
        private double _v;

        // 对照:同一块黑板上普通写一次 double 端口的代价(版本号字典的开销,跟手势无关)
        [Benchmark(Description = "对照:黑板写一次 double 端口")]
        public void PlainBoardWrite()
        {
            var b = _host.Board!;
            using (b.AcquireWriteLock()) b.WriteIfChanged(_plain, _v += 1);
        }

        [Benchmark(Description = "拖动中:单次移动(写手势端口)")]
        public void DragMove()
        {
            _x = _x > 1200 ? 60 : _x + 7.3f;
            _router.OnMove(new PointerInput(new HevoPoint(_x, 200), new HevoPoint(_x, 200), PointerButton.None, PointerModifiers.None, 2));
        }
    }
}
