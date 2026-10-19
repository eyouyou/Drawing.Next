using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.PythonNet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// PythonNet 封送 / 模块加载 / 超时 / 路径守护的回归测试,不需要真 Python(真 Python 的部分在 <see cref="PythonNetHardeningE2ETests"/>)。
    /// </summary>
    public sealed class PythonNetHardeningTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hevo_pyhard_{Guid.NewGuid():N}");

        public PythonNetHardeningTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        // ── 强类型返回值按委托返回类型放宽 ──────────────────────────────────

        // Python int 解包成 long,float 解包成 double;委托声明的返回类型和它不一致时要按数值放宽,
        // 而不是直接 (double)(object)long 拆箱失败。
        [Fact]
        public void TypedReturn_LongFromPython_WidensToDouble()
        {
            var (registry, rt) = NewRegistry();
            var file = WritePy("w1.py", "def f(x): pass\n");
            registry.RegisterModule("w1", file, "f", "(ReadOnlyMemory[double]) -> double");
            rt.Last!.StubResult = 7L;

            var del = (Func<ReadOnlyMemory<double>, double>)registry.TryGet("w1")!;
            Assert.Equal(7.0, del(new[] { 1.0 }));
        }

        [Fact]
        public void TypedReturn_DoubleIntegral_NarrowsToInt()
        {
            var (registry, rt) = NewRegistry();
            var file = WritePy("w2.py", "def f(x): pass\n");
            registry.RegisterModule("w2", file, "f", "(ReadOnlyMemory[double]) -> int");
            rt.Last!.StubResult = 3.0;

            var del = (Func<ReadOnlyMemory<double>, int>)registry.TryGet("w2")!;
            Assert.Equal(3, del(new[] { 1.0 }));
        }

        [Fact]
        public void TypedReturn_DoubleWithFraction_ToInt_Throws()
        {
            var (registry, rt) = NewRegistry();
            var file = WritePy("w3.py", "def f(x): pass\n");
            registry.RegisterModule("w3", file, "f", "(ReadOnlyMemory[double]) -> int");
            rt.Last!.StubResult = 3.5;

            var del = (Func<ReadOnlyMemory<double>, int>)registry.TryGet("w3")!;
            Assert.Throws<InvalidCastException>(() => del(new[] { 1.0 }));
        }

        // None 对 double 返回值按缺失值处理(NaN),跟指标列的缺失值约定一致。
        [Fact]
        public void TypedReturn_None_ToDouble_IsNaN()
        {
            var (registry, rt) = NewRegistry();
            var file = WritePy("w4.py", "def f(x): pass\n");
            registry.RegisterModule("w4", file, "f", "(ReadOnlyMemory[double]) -> double");
            rt.Last!.StubResult = null;

            var del = (Func<ReadOnlyMemory<double>, double>)registry.TryGet("w4")!;
            Assert.True(double.IsNaN(del(new[] { 1.0 })));
        }

        // int64 ndarray(np.arange 之类)解包成 ROM<long>,声明 ROM[double] 时逐元素转换。
        [Fact]
        public void TypedReturn_LongColumn_WidensToDoubleColumn()
        {
            var (registry, rt) = NewRegistry();
            var file = WritePy("w5.py", "def f(x): pass\n");
            registry.RegisterModule("w5", file, "f", "(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]");
            rt.Last!.StubResult = new ReadOnlyMemory<long>(new long[] { 1, 2, 3 });

            var del = (Func<ReadOnlyMemory<double>, ReadOnlyMemory<double>>)registry.TryGet("w5")!;
            Assert.Equal(new[] { 1.0, 2.0, 3.0 }, del(new[] { 1.0 }).ToArray());
        }

        // 增量协议 (output, state):tuple 各元素同样按声明类型放宽。
        [Fact]
        public void TupleReturn_ElementsWidenToDeclaredTypes()
        {
            var (registry, rt) = NewRegistry();
            var file = WritePy("w6.py", "def f(x): pass\n");
            registry.RegisterModule("w6", file, "f", "(ReadOnlyMemory[double]) -> Tuple[double, long]");
            rt.Last!.StubResult = new object?[] { 2L, 5.0 };

            var del = (Func<ReadOnlyMemory<double>, (double, long)>)registry.TryGet("w6")!;
            Assert.Equal((2.0, 5L), del(new[] { 1.0 }));
        }

        // ── 热重载:登记源文件和 inputs,删掉的 handler 摘除 ──────────────────

        [Fact]
        public void HotReload_InitialScan_RegistersInputsAndSourceFile()
        {
            var (registry, _) = NewRegistry();
            var file = WritePy("hr1.py", """
                from hevo_indicators import register
                @register('hr1_spread', signature='(ReadOnlyMemory[double], ReadOnlyMemory[double]) -> ReadOnlyMemory[double]', inputs=['a', 'b'])
                def spread(a, b): return a
                """);

            using (registry.EnableHotReload(_root))
            {
                Assert.NotNull(registry.TryGet("hr1_spread"));
                Assert.Equal(new[] { "a", "b" }, registry.GetInputNames("hr1_spread"));
            }

            // 源文件登记了,删除时能按文件摘掉
            registry.UnregisterBySourceFile(file);
            Assert.Null(registry.TryGet("hr1_spread"));
            Assert.Null(registry.GetInputNames("hr1_spread"));
        }

        [Fact]
        public void HotReload_HandlerRemovedFromFile_IsUnregistered()
        {
            var (registry, rt) = NewRegistry();
            var file = WritePy("hr2.py", """
                from hevo_indicators import register
                @register('hr2_a', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def a(x): return x
                @register('hr2_b', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def b(x): return x
                """);
            registry.ReloadFile(file);
            Assert.NotNull(registry.TryGet("hr2_a"));
            Assert.NotNull(registry.TryGet("hr2_b"));
            int importsBefore = rt.ImportCount;

            File.WriteAllText(file, """
                from hevo_indicators import register
                @register('hr2_a', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]', inputs=['close'])
                def a(x): return x
                """);
            var result = registry.ReloadFile(file);

            Assert.Equal(1, result.Registered);
            Assert.Empty(result.Errors);
            Assert.NotNull(registry.TryGet("hr2_a"));
            Assert.Equal(new[] { "close" }, registry.GetInputNames("hr2_a"));
            Assert.Null(registry.TryGet("hr2_b"));
            // 一个文件只重新导入一次,不是每个 handler 导一次
            Assert.Equal(importsBefore + 1, rt.ImportCount);
        }

        [Fact]
        public void HotReload_ImportFails_OldHandlersKeepServing()
        {
            var rt = new FakeRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(rt).Initialize(new PythonSandboxOptions { AllowedRootDirectory = _root });
            var file = WritePy("hr3.py", """
                from hevo_indicators import register
                @register('hr3', signature='(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]')
                def a(x): return x
                """);
            registry.ReloadFile(file);
            var before = registry.TryGet("hr3");

            rt.FailImports = true;
            Assert.ThrowsAny<Exception>(() => registry.ReloadFile(file));
            Assert.Same(before, registry.TryGet("hr3"));
        }

        // ── 注册表并发读写 ────────────────────────────────────────────────

        [Fact]
        public void HandlerRegistry_ConcurrentRegisterAndRead_DoesNotThrow()
        {
            var registry = new BlueprintHandlerRegistry();
            var stop = DateTime.UtcNow.AddMilliseconds(500);
            var errors = new List<Exception>();
            var tasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
            {
                try
                {
                    int i = 0;
                    while (DateTime.UtcNow < stop)
                    {
                        var name = $"h{t}_{i++ % 64}";
                        registry.RegisterDelegate(name, new Action(() => { }), new[] { "x" });
                        registry.TryGet(name);
                        registry.GetInputNames(name);
                        foreach (var _ in registry.EnumerateAllHandlers()) { }
                        registry.Unregister(name);
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            })).ToArray();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(15));
            Assert.Empty(errors);
        }

        // ── 超时熔断 ──────────────────────────────────────────────────────

        [Fact]
        public void Breaker_WhileOrphanRunning_FailsFastWithoutStartingNewCall()
        {
            var breaker = new PythonCallBreaker();
            using var release = new ManualResetEventSlim(false);
            int started = 0;

            Assert.Throws<TimeoutException>(() => PerCallTimeoutWatchdog.RunWithTimeout(50, () =>
            {
                Interlocked.Increment(ref started);
                release.Wait(TimeSpan.FromSeconds(10));
                return 0;
            }, breaker));

            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<PythonCallRejectedException>(() =>
                PerCallTimeoutWatchdog.RunWithTimeout(5_000, () => { Interlocked.Increment(ref started); return 1; }, breaker));
            Assert.IsAssignableFrom<TimeoutException>(ex);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"应立即拒绝,实际 {sw.ElapsedMilliseconds}ms");
            Assert.Equal(1, started);

            // 孤儿调用跑完后恢复
            release.Set();
            SpinWait.SpinUntil(() => !breaker.HasOrphan, TimeSpan.FromSeconds(10));
            Assert.Equal(2, PerCallTimeoutWatchdog.RunWithTimeout(10_000, () => 2, breaker));
        }

        // 超时的那次调用卡在事件上,断言超时之后才放行:不靠 20ms / 200ms 的时间差,
        // 2 核 CI 上等待线程被挂起超过 200ms 时 sleep 已经睡完,Wait 会拿到"已完成"而不超时
        private static void TimeOutOnce(PythonCallBreaker breaker)
        {
            using var release = new ManualResetEventSlim(false);
            Assert.Throws<TimeoutException>(() => PerCallTimeoutWatchdog.RunWithTimeout(20, () => { release.Wait(TimeSpan.FromSeconds(10)); return 0; }, breaker));
            release.Set();
            SpinWait.SpinUntil(() => !breaker.HasOrphan, TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void Breaker_TripsAfterConsecutiveTimeouts()
        {
            var breaker = new PythonCallBreaker();
            for (int i = 0; i < PythonCallBreaker.TripAfterConsecutiveTimeouts; i++) TimeOutOnce(breaker);

            Assert.True(breaker.IsTripped);
            Assert.Throws<PythonCallRejectedException>(() => PerCallTimeoutWatchdog.RunWithTimeout(10_000, () => 1, breaker));
        }

        [Fact]
        public void Breaker_SuccessResetsConsecutiveCount()
        {
            var breaker = new PythonCallBreaker();
            for (int i = 0; i < PythonCallBreaker.TripAfterConsecutiveTimeouts - 1; i++) TimeOutOnce(breaker);
            Assert.Equal(1, PerCallTimeoutWatchdog.RunWithTimeout(10_000, () => 1, breaker));
            TimeOutOnce(breaker);
            Assert.False(breaker.IsTripped);
        }

        [Fact]
        public void Timeout_InvokesOnTimeoutCallback()
        {
            int called = 0;
            using var release = new ManualResetEventSlim(false);
            Assert.Throws<TimeoutException>(() => PerCallTimeoutWatchdog.RunWithTimeout(20, () => { release.Wait(TimeSpan.FromSeconds(10)); return 0; },
                new PythonCallBreaker(), onTimeout: () => Interlocked.Increment(ref called)));
            release.Set();
            Assert.Equal(1, called);
        }

        // ── AllowedRootDirectory:前缀比较带分隔符 ─────────────────────────

        [Fact]
        public void AllowedRoot_SiblingWithSamePrefix_IsRejected()
        {
            var root = Path.Combine(_root, "ind");
            var sibling = Path.Combine(_root, "ind_evil");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);
            var evil = Path.Combine(sibling, "x.py");
            File.WriteAllText(evil, "def f(x): return x\n");

            var registry = new PythonHandlerRegistry().UseRuntime(new FakeRuntime())
                .Initialize(new PythonSandboxOptions { AllowedRootDirectory = root });

            Assert.Throws<UnauthorizedAccessException>(() =>
                registry.RegisterModule("evil", evil, "f", "(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]"));
        }

        [Fact]
        public void AllowedRoot_FileInsideRoot_IsAccepted()
        {
            var root = Path.Combine(_root, "ind") + Path.DirectorySeparatorChar;   // 末尾带分隔符也要认
            Directory.CreateDirectory(root);
            var ok = Path.Combine(root, "sub", "x.py");
            Directory.CreateDirectory(Path.GetDirectoryName(ok)!);
            File.WriteAllText(ok, "def f(x): return x\n");

            var registry = new PythonHandlerRegistry().UseRuntime(new FakeRuntime())
                .Initialize(new PythonSandboxOptions { AllowedRootDirectory = root });
            registry.RegisterModule("ok", ok, "f", "(ReadOnlyMemory[double]) -> ReadOnlyMemory[double]");
            Assert.NotNull(registry.TryGet("ok"));
        }

        // ── PythonHomeResolver ────────────────────────────────────────────

        [Fact]
        public void HomeResolver_PicksHighestMinorVersion_NotLexicographic()
        {
            var dir = Path.Combine(_root, "py");
            Directory.CreateDirectory(dir);
            foreach (var n in new[] { "python3.dll", "python39.dll", "python312.dll", "python310.dll", "python313.dll" })
                File.WriteAllBytes(Path.Combine(dir, n), Array.Empty<byte>());

            var hit = PythonHomeResolver.ProbeInDirectory(dir);
            Assert.Equal("python312.dll", Path.GetFileName(hit));
        }

        [Fact]
        public void HomeResolver_PortableProbe_OnlyLooksInsideAppDirectory()
        {
            var parent = Path.Combine(_root, "repo");
            var app = Path.Combine(parent, "bin", "app");
            Directory.CreateDirectory(Path.Combine(parent, "Python312"));   // 祖先目录里的,不该被看到
            Directory.CreateDirectory(Path.Combine(app, "Python311"));

            var dirs = PythonHomeResolver.EnumeratePortableDirs(app).ToList();
            Assert.Single(dirs);
            Assert.Equal(Path.Combine(app, "Python311"), dirs[0]);
        }

        [Fact]
        public void HomeResolver_InstallDirs_SortedByVersionDescending()
        {
            var programs = Path.Combine(_root, "Programs");
            foreach (var n in new[] { "Python39", "Python312", "Python310", "Python311-32" })
                Directory.CreateDirectory(Path.Combine(programs, n));

            var ordered = PythonHomeResolver.OrderInstallDirsByVersion(Directory.EnumerateDirectories(programs, "Python3*"))
                .Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "Python312", "Python311-32", "Python310", "Python39" }, ordered);
        }

        // ── helpers ─────────────────────────────────────────────────────

        private (PythonHandlerRegistry, FakeRuntime) NewRegistry()
        {
            var rt = new FakeRuntime();
            var registry = new PythonHandlerRegistry().UseRuntime(rt)
                .Initialize(new PythonSandboxOptions { AllowedRootDirectory = _root, PerCallTimeoutMs = 0 });
            return (registry, rt);
        }

        private string WritePy(string name, string text)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, text);
            return path;
        }

        private sealed class FakeRuntime : IPythonRuntime
        {
            public FakeModule? Last { get; private set; }
            public int ImportCount;
            public bool FailImports;

            public void Initialize(PythonSandboxOptions options) { }

            public IPythonModule ImportModule(string moduleName, string filePath)
            {
                if (FailImports)
                    throw new PythonDiagnosticsException("boom", "SyntaxError", "Traceback", filePath);
                Interlocked.Increment(ref ImportCount);
                return Last = new FakeModule(moduleName);
            }

            public void Shutdown() { }
        }

        private sealed class FakeModule : IPythonModule
        {
            public FakeModule(string name) { Name = name; }
            public string Name { get; }
            public object? StubResult { get; set; }
            public object? Invoke(string functionName, params object?[] args) => StubResult;
            public bool HasFunction(string functionName) => true;
            public IReadOnlyList<PythonHandlerDescriptor> ListRegisteredHandlers() => Array.Empty<PythonHandlerDescriptor>();
        }
    }
}
