using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Linked;
using Hevo.Charting.LowCode;
using Hevo.Charting.WorkFlow;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// 黑板内的 Watch 表(AddWatch):提交时按注册顺序只跑脏的,重入深度优先,异常后不重跑,退订不拿锁。
    /// 这几条语义跟以前"每次提交把所有 Watch 的提交回调调一遍、各自看自己脏没脏"一致,用例把它们逐条锁住。
    /// </summary>
    public sealed class BlackboardWatchTableTests
    {
        private readonly DataPort<double> _a = new("WT_A");
        private readonly DataPort<double> _b = new("WT_B");
        private readonly DataPort<double> _c = new("WT_C");

        private static void WriteNow<T>(DataBlackboard board, DataPort<T> port, T value)
        {
            using (board.AcquireWriteLock()) board.WriteIfChanged(port, value);
        }

        [Fact]
        public void OnlyWatchesOfWrittenPort_Run()
        {
            using var board = new DataBlackboard();
            var calls = new int[50];
            var ports = new DataPort<double>[50];
            for (int i = 0; i < ports.Length; i++)
            {
                int k = i;
                ports[i] = new DataPort<double>($"WT_P{i}");
                board.AddWatch(new object[] { ports[i] }, _ => calls[k]++);
            }

            WriteNow(board, ports[17], 1.0);

            Assert.Equal(1, calls[17]);
            Assert.Equal(1, calls.Sum());
        }

        [Fact]
        public void DirtyWatches_RunInRegistrationOrder_OncePerCommit()
        {
            using var board = new DataBlackboard();
            var order = new List<string>();
            board.AddWatch(new object[] { _b }, _ => order.Add("w1:b"));
            board.AddWatch(new object[] { _a, _b }, _ => order.Add("w2:ab"));
            board.AddWatch(new object[] { _a }, _ => order.Add("w3:a"));

            using (board.BeginTransaction())
            {
                board.WriteIfChanged(_a, 1);
                board.WriteIfChanged(_b, 1);
            }

            Assert.Equal(new[] { "w1:b", "w2:ab", "w3:a" }, order);
        }

        [Fact]
        public void WriteInCallback_RunsDownstreamDepthFirst_DerivedValueReadableRightAway()
        {
            using var board = new DataBlackboard();
            // 下游先注册:外层扫表时它排在前面且还不脏,只能靠回调里嵌套提交跑到
            board.AddWatch(new object[] { _b }, bb => bb.WriteIfChanged(_c, bb.Read(_b) + 1));
            board.AddWatch(new object[] { _a }, bb => bb.WriteIfChanged(_b, bb.Read(_a) * 2));

            using (board.AcquireWriteLock())
            {
                board.WriteIfChanged(_a, 5);
                Assert.Equal(11, board.Read(_c));
            }
        }

        [Fact]
        public void CallbackThrows_NextCommitRunsEachWatchOnce()
        {
            using var board = new DataBlackboard();
            int first = 0, second = 0;
            bool throwOnce = true;
            board.AddWatch(new object[] { _a }, _ =>
            {
                first++;
                if (throwOnce) { throwOnce = false; throw new InvalidOperationException("boom"); }
            });
            board.AddWatch(new object[] { _a }, _ => second++);

            Assert.Throws<InvalidOperationException>(() => WriteNow(board, _a, 1));
            Assert.Equal(1, first);
            Assert.Equal(0, second); // 排在抛异常那项后面:留到下次提交

            WriteNow(board, _a, 2);
            Assert.Equal(2, first);
            Assert.Equal(1, second);
        }

        [Fact]
        public void DisposedWatch_NotCalled_EvenWhenDisposedByEarlierWatchInSameCommit()
        {
            using var board = new DataBlackboard();
            int victim = 0;
            IDisposable? victimHandle = null;
            board.AddWatch(new object[] { _a }, _ => victimHandle!.Dispose());
            victimHandle = board.AddWatch(new object[] { _a }, _ => victim++);

            WriteNow(board, _a, 1);
            Assert.Equal(0, victim);

            WriteNow(board, _a, 2);
            Assert.Equal(0, victim);
        }

        [Fact]
        public void ManyAddsAndRemoves_TableStaysConsistent()
        {
            using var board = new DataBlackboard();
            var calls = new int[10];
            var handles = new IDisposable[10];
            for (int i = 0; i < 10; i++)
            {
                int k = i;
                handles[i] = board.AddWatch(new object[] { _a, _b }, _ => calls[k]++);
            }
            for (int i = 1; i < 10; i += 2) handles[i].Dispose();

            WriteNow(board, _a, 1); // 这次提交顺手压缩
            int late = 0;
            board.AddWatch(new object[] { _b }, _ => late++);
            WriteNow(board, _b, 1);

            for (int i = 0; i < 10; i++) Assert.Equal(i % 2 == 0 ? 2 : 0, calls[i]);
            Assert.Equal(1, late);
        }

        [Fact]
        public void DisposeWatch_WhileHoldingReadLock_DoesNotThrow()
        {
            using var board = new DataBlackboard();
            int calls = 0;
            var handle = board.AddWatch(new object[] { _a }, _ => calls++);

            using (board.AcquireReadLock())
            {
                Assert.Null(Record.Exception(() => handle.Dispose()));
            }

            WriteNow(board, _a, 1);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void FlowWatch_UnsubscribeWhileHoldingReadLock_DoesNotThrow_AndStopsCallbacks()
        {
            using var board = new DataBlackboard();
            int calls = 0;
            IWorkflow<DataBlackboard> source = new WorkflowEngine<DataBlackboard>((next, _) => { next(board); return DisposableAction.Empty; });
            var sub = source.Watch(new object[] { _a, _b }, _ => calls++).Subscribe(_ => { });
            Assert.Equal(1, calls); // 订阅时初跑一次

            using (board.BeginTransaction())
            {
                board.WriteIfChanged(_a, 1);
                board.WriteIfChanged(_b, 1);
            }
            Assert.Equal(2, calls); // 一次提交写了两个关心的端口也只跑一次

            using (board.AcquireReadLock())
            {
                Assert.Null(Record.Exception(() => sub.Dispose()));
            }

            WriteNow(board, _a, 2);
            Assert.Equal(2, calls);
        }

        [Fact]
        public void LinkedMirror_ViewportWrittenInMasterTransaction_PaneGetsFinalValues()
        {
            var context = new LinkedChartContext();
            var master = new DataBlackboard();
            var pane = new DataBlackboard();
            context.AttachBoard(master);
            context.AttachBoard(pane);
            var vp = context.SharedViewport;

            using (master.BeginTransaction())
            {
                master.WriteIfChanged(vp.LogicalLength, 100);
                master.WriteIfChanged(vp.UserRange, new RealRange(10, 50));
                master.WriteIfChanged(vp.ActiveRange, new RealRange(10, 40));
                master.WriteIfChanged(vp.ActiveRange, new RealRange(20, 60));
            }

            using (pane.AcquireReadLock())
            {
                Assert.Equal(100, pane.Read(vp.LogicalLength));
                Assert.Equal(new RealRange(10, 50), pane.Read(vp.UserRange));
                Assert.Equal(new RealRange(20, 60), pane.Read(vp.ActiveRange));
            }
        }

        // ── A2:版本号要持锁读;CombineLatest / Join 锁外的两处自己进读锁 ──────────────

#if DEBUG
        [Fact]
        public void Debug_GetVersionWithoutLock_Throws()
        {
            using var board = new DataBlackboard();
            Assert.Throws<InvalidOperationException>(() => board.GetVersion(_a));
            Assert.Throws<InvalidOperationException>(() => board.GetIdentityVersion(_a));
        }
#endif

        // 不走 Feature 装配(装配路径见 FeatureComposeOperatorTests),
        // 这里只验锁外读版本号这一条,用一个不带图表的渲染流壳直接挂
        private sealed class BareRenderFlow : IRenderFlow<DataBlackboard>
        {
            private readonly IWorkflow<DataBlackboard> _inner;
            public BareRenderFlow(IWorkflow<DataBlackboard> inner) => _inner = inner;
            public ChartCell Chart => null!;
            public IDisposable Subscribe(Action<DataBlackboard> onNext, Action<Exception>? onError = null) => _inner.Subscribe(onNext, onError);
            public IRenderFlow<TOut> Wrap<TOut>(IWorkflow<TOut> next) => (IRenderFlow<TOut>)(object)new BareRenderFlow((IWorkflow<DataBlackboard>)(object)next);
        }

        [Fact]
        public void CombineLatest_InAsyncCallback_PassesOnceAllPortsWritten()
        {
            using var board = new DataBlackboard();
            var trigger = new WorkflowTrigger<DataBlackboard>();
            using var fired = new ManualResetEventSlim(false);
            int calls = 0;
            using var sub = new BareRenderFlow(trigger)
                .CombineLatest(new object[] { _a, _b }, _ => { Interlocked.Increment(ref calls); fired.Set(); })
                .Subscribe(_ => { });
            trigger.Push(board);

            board.Write(_a, 1.0);
            Assert.False(fired.Wait(200), "只写了一个端口不该放行");

            board.Write(_b, 1.0);
            Assert.True(fired.Wait(5000), "两个端口都写过后应放行");
        }

        [Fact]
        public void Join_FiltersOutBoardUntilAllPortsWritten()
        {
            using var board = new DataBlackboard();
            var trigger = new WorkflowTrigger<DataBlackboard>();
            int passed = 0;
            using var sub = new BareRenderFlow(trigger).Join(_a, _b).Subscribe(_ => passed++);

            trigger.Push(board); // 端口都没写:挡住
            board.Write(_a, 1.0);
            trigger.Push(board); // 只写了一个:挡住
            Assert.Equal(0, passed);

            board.Write(_b, 1.0);
            trigger.Push(board);
            Assert.Equal(1, passed);
        }

        // ── A3:事务作用域复用 ────────────────────────────────────────────────────

        [Fact]
        public void BeginTransaction_Repeated_AllocatesNothing_AndNestingStillWorks()
        {
            using var board = new DataBlackboard();
            using (board.BeginTransaction()) { } // 预热

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                using (board.BeginTransaction()) { }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);

            int commits = 0;
            board.AddWatch(new object[] { _a }, _ => commits++);
            using (board.BeginTransaction())
            {
                using (board.BeginTransaction()) board.WriteIfChanged(_a, 1);
                Assert.Equal(0, commits);
            }
            Assert.Equal(1, commits);
        }

        // ── A4 划线:Features 命名空间只用黑板的公开成员 ────────────────────────────

        /// <summary>
        /// 业务照着 Features/ 抄代码,抄过去撞上 internal 就编不过。测试工程有 InternalsVisibleTo,
        /// Feature 误用 internal 成员照样能编译,所以扫方法体 IL 断言可见性,不能靠编不编得过。
        /// </summary>
        [Fact]
        public void FeaturesNamespace_ReferencesOnlyPublicBlackboardMembers()
        {
            var asm = typeof(DataBlackboard).Assembly;
            var featureTypes = asm.GetTypes()
                .Where(t => t.Namespace == "Hevo.Charting.Features" || (t.Namespace?.StartsWith("Hevo.Charting.Features.") ?? false))
                .ToArray();
            Assert.NotEmpty(featureTypes);

            var referenced = new HashSet<MemberInfo>();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in featureTypes)
            {
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    foreach (var member in IlReader.ReferencedMembers(method))
                    {
                        if (IsBlackboardMember(member)) referenced.Add(member);
                    }
                }
            }

            // 防空跑:Features 里肯定读写过黑板
            Assert.Contains(referenced, m => m.Name == nameof(DataBlackboard.Read));

            var leaks = referenced.Where(m => !IsPublicSurface(m)).Select(m => $"{m.DeclaringType!.Name}.{m.Name}").Distinct().ToList();
            Assert.True(leaks.Count == 0, "Features 命名空间引用了黑板的非公开成员:" + string.Join(", ", leaks));
        }

        private static bool IsBlackboardMember(MemberInfo m)
        {
            for (var t = m.DeclaringType; t != null; t = t.DeclaringType)
                if (t == typeof(DataBlackboard)) return true;
            return false;
        }

        private static bool IsPublicSurface(MemberInfo m)
        {
            bool memberPublic = m switch
            {
                MethodBase mb => mb.IsPublic,
                FieldInfo f => f.IsPublic,
                _ => true,
            };
            if (!memberPublic) return false;
            for (var t = m.DeclaringType; t != null; t = t.DeclaringType)
                if (!(t.IsPublic || t.IsNestedPublic)) return false;
            return true;
        }

        /// <summary>只认操作数是成员令牌的指令(call / callvirt / newobj / ldfld / ldftn / ldtoken …),其余按操作数长度跳过。</summary>
        private static class IlReader
        {
            private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (OpCode)f.GetValue(null)!)
                .ToDictionary(o => o.Value);

            public static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null) yield break;
                var typeArgs = method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
                var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

                int pos = 0;
                while (pos < il.Length)
                {
                    short value = il[pos++];
                    if (value == 0xFE) value = (short)(0xFE00 | il[pos++]);
                    var op = OpCodesByValue[value];
                    switch (op.OperandType)
                    {
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                            pos += 1;
                            break;
                        case OperandType.InlineVar:
                            pos += 2;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            pos += 8;
                            break;
                        case OperandType.InlineSwitch:
                            int n = BitConverter.ToInt32(il, pos);
                            pos += 4 + 4 * n;
                            break;
                        case OperandType.InlineMethod:
                        case OperandType.InlineField:
                        case OperandType.InlineTok:
                            int token = BitConverter.ToInt32(il, pos);
                            pos += 4;
                            MemberInfo? member = null;
                            try { member = method.Module.ResolveMember(token, typeArgs, methodArgs); }
                            catch (ArgumentException) { }
                            if (member is MethodBase || member is FieldInfo) yield return member;
                            break;
                        default:
                            pos += 4;
                            break;
                    }
                }
            }
        }
    }
}
