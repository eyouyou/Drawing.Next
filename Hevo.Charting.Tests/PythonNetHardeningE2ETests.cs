using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 真 Python 下的封送与模块加载回归:numpy 标量、强类型标量返回值、按路径导入、超时中断。
    /// 每个用例的 .py 放在自己的子目录里,不被别的用例 AutoDiscoverDirectory(只扫顶层)顺带注册。
    /// </summary>
    [Collection(nameof(RealPythonCollection))]
    public sealed class PythonNetHardeningE2ETests
    {
        private readonly RealPythonFixture _fx;

        public PythonNetHardeningE2ETests(RealPythonFixture fx) => _fx = fx;

        // ── numpy 标量 ────────────────────────────────────────────────────

        // np.float64 / np.int64 也有 dtype 和 size,旧实现把它们当 ndarray 解成长度 1 的列,强类型标量委托拆箱失败。
        [Fact]
        public void NumpyFloat64Scalar_ToDoubleReturn()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("np_scalar_f64.py", """
                import numpy as np
                def total(x):
                    return np.asarray(x, dtype=np.float64).sum()
                """);
            _fx.Registry!.RegisterModule("np_scalar_f64", file, "total", "(ReadOnlyMemory[double]) -> double");
            var del = (Func<ReadOnlyMemory<double>, double>)_fx.Registry.TryGet("np_scalar_f64")!;
            Assert.Equal(6.0, del(new[] { 1.0, 2.0, 3.0 }), 9);
        }

        [Fact]
        public void NumpyInt64Scalar_ToIntReturn()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("np_scalar_i64.py", """
                import numpy as np
                def count(x):
                    return np.int64(len(x))
                """);
            _fx.Registry!.RegisterModule("np_scalar_i64", file, "count", "(ReadOnlyMemory[double]) -> int");
            var del = (Func<ReadOnlyMemory<double>, int>)_fx.Registry.TryGet("np_scalar_i64")!;
            Assert.Equal(3, del(new[] { 1.0, 2.0, 3.0 }));
        }

        [Fact]
        public void PythonIntScalar_ToDoubleReturn()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("py_int_ret.py", """
                def count(x):
                    return len(x)
                """);
            _fx.Registry!.RegisterModule("py_int_ret", file, "count", "(ReadOnlyMemory[double]) -> double");
            var del = (Func<ReadOnlyMemory<double>, double>)_fx.Registry.TryGet("py_int_ret")!;
            Assert.Equal(3.0, del(new[] { 1.0, 2.0, 3.0 }));
        }

        [Fact]
        public void NumpyScalarsInsideDict_UnboxToClrScalars()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("np_dict.py", """
                import numpy as np
                def stats(x):
                    a = np.asarray(x, dtype=np.float64)
                    return {'mean': a.mean(), 'n': np.int64(a.size), 'f32': np.float32(0.5), 'flag': np.bool_(True), 'zero_d': np.array(4.0)}
                """);
            _fx.Registry!.RegisterPythonFunction("np_dict", file, "stats");
            var del = (Func<object?[], object?>)_fx.Registry.TryGet("np_dict")!;
            var d = Assert.IsType<Dictionary<string, object?>>(del(new object?[] { new ReadOnlyMemory<double>(new[] { 1.0, 3.0 }) }));
            Assert.Equal(2.0, Assert.IsType<double>(d["mean"]));
            Assert.Equal(2L, Assert.IsType<long>(d["n"]));
            Assert.Equal(0.5, Assert.IsType<double>(d["f32"]));
            Assert.Equal(1L, Assert.IsType<long>(d["flag"]));
            Assert.Equal(4.0, Assert.IsType<double>(d["zero_d"]));
        }

        [Fact]
        public void IncrementalTuple_WithNumpyScalarState()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("np_tuple.py", """
                import numpy as np
                def step(x):
                    a = np.asarray(x, dtype=np.float64)
                    return a * 2.0, a.sum()
                """);
            _fx.Registry!.RegisterModule("np_tuple", file, "step",
                "(ReadOnlyMemory[double]) -> Tuple[ReadOnlyMemory[double], double]");
            var del = (Func<ReadOnlyMemory<double>, (ReadOnlyMemory<double>, double)>)_fx.Registry.TryGet("np_tuple")!;
            var (col, state) = del(new[] { 1.0, 2.0 });
            Assert.Equal(new[] { 2.0, 4.0 }, col.ToArray());
            Assert.Equal(3.0, state);
        }

        // ── 按路径导入 ────────────────────────────────────────────────────

        // 文件名和标准库同名:旧实现 Py.Import("statistics") 拿到的是标准库,找不到用户函数。
        [Fact]
        public void ModuleNamedLikeStdlib_LoadsUserFile()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("statistics.py", """
                def hevo_user_fn(x):
                    return 42
                """);
            _fx.Registry!.RegisterModule("stdlib_named", file, "hevo_user_fn", "(ReadOnlyMemory[double]) -> int");
            var del = (Func<ReadOnlyMemory<double>, int>)_fx.Registry.TryGet("stdlib_named")!;
            Assert.Equal(42, del(new[] { 1.0 }));
        }

        // 两个目录里同名文件:旧实现按文件名进 sys.modules,第二个会绑到第一个(或 reload 成第一个)的代码。
        [Fact]
        public void SameFileNameInDifferentDirs_EachBindsOwnCode()
        {
            if (!_fx.Available) return;
            var f1 = WriteIsolated("dup_name.py", "def which(x):\n    return 1\n");
            var f2 = WriteIsolated("dup_name.py", "def which(x):\n    return 2\n");
            _fx.Registry!.RegisterModule("dup_1", f1, "which", "(ReadOnlyMemory[double]) -> int");
            _fx.Registry.RegisterModule("dup_2", f2, "which", "(ReadOnlyMemory[double]) -> int");

            var d1 = (Func<ReadOnlyMemory<double>, int>)_fx.Registry.TryGet("dup_1")!;
            var d2 = (Func<ReadOnlyMemory<double>, int>)_fx.Registry.TryGet("dup_2")!;
            Assert.Equal(1, d1(new[] { 0.0 }));
            Assert.Equal(2, d2(new[] { 0.0 }));
        }

        // 热重载:改文件后 ReloadFile 拿到新代码,删掉的 handler 摘除,inputs 一并更新。
        [Fact]
        public void ReloadFile_PicksUpNewCodeAndInputs()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("reload_v.py", """
                from hevo_indicators import register
                @register('reload_v', signature='(ReadOnlyMemory[double]) -> double')
                def v(x):
                    return 1.0
                @register('reload_gone', signature='(ReadOnlyMemory[double]) -> double')
                def g(x):
                    return 0.0
                """);
            _fx.Registry!.ReloadFile(file);
            Assert.Equal(1.0, ((Func<ReadOnlyMemory<double>, double>)_fx.Registry.TryGet("reload_v")!)(new[] { 0.0 }));

            File.WriteAllText(file, """
                from hevo_indicators import register
                @register('reload_v', signature='(ReadOnlyMemory[double]) -> double', inputs=['close'])
                def v(close):
                    return 2.0
                """);
            var r = _fx.Registry.ReloadFile(file);
            Assert.Empty(r.Errors);
            Assert.Equal(2.0, ((Func<ReadOnlyMemory<double>, double>)_fx.Registry.TryGet("reload_v")!)(new[] { 0.0 }));
            Assert.Equal(new[] { "close" }, _fx.Registry.GetInputNames("reload_v"));
            Assert.Null(_fx.Registry.TryGet("reload_gone"));
        }

        // ── 超时中断 ──────────────────────────────────────────────────────

        // 纯 Python 死循环超时后要被中断(KeyboardInterrupt),而不是作为孤儿一直跑下去占 GIL / CPU。
        [Fact]
        public void Timeout_InterruptsRunawayPythonLoop()
        {
            if (!_fx.Available) return;
            var file = WriteIsolated("spin.py", """
                counter = 0
                def spin(x):
                    global counter
                    while True:
                        counter += 1
                def read(x):
                    return counter
                """);
            _fx.Registry!.RegisterModule("spin_forever", file, "spin", "(ReadOnlyMemory[double]) -> double");
            _fx.Registry.RegisterModule("spin_read", file, "read", "(ReadOnlyMemory[double]) -> long");
            var spin = (Func<ReadOnlyMemory<double>, double>)_fx.Registry.TryGet("spin_forever")!;
            var read = (Func<ReadOnlyMemory<double>, long>)_fx.Registry.TryGet("spin_read")!;

            Assert.Throws<TimeoutException>(() => spin(new[] { 0.0 }));

            // 中断是异步投递的,给它一点时间生效后计数器应停止增长
            long last = -1, now = read(new[] { 0.0 });
            var sw = Stopwatch.StartNew();
            while (now != last && sw.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(200);
                last = now;
                now = read(new[] { 0.0 });
            }
            Assert.Equal(last, now);
        }

        private string WriteIsolated(string fileName, string text)
        {
            var dir = Path.Combine(_fx.IndicatorsDir, "hardening", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, text);
            return path;
        }
    }
}
