using System.Runtime.CompilerServices;

namespace Hevo.Charting.Core
{
    /// <summary>
    /// UseMemo 寻址 key:[CallerFilePath] 注入的 file 是 intern string(同一编译单元同一字面量复用同一 instance),
    /// Equals 走 ReferenceEquals + int 比较,GetHashCode 用 identity hash —— 跳过 string 内容 hash + 内容比较,
    /// 稳态 0 GC + 微秒级查找。
    /// </summary>
    internal readonly struct HookKey : IEquatable<HookKey>
    {
        public readonly string File;
        public readonly int Line;

        public HookKey(string file, int line)
        {
            File = file;
            Line = line;
        }

        // ReferenceEquals 命中 99.9%(intern string);极端跨编译单元同路径不命中时落 string.Equals 兜底。
        public bool Equals(HookKey other) =>
            Line == other.Line
            && (ReferenceEquals(File, other.File) || string.Equals(File, other.File, StringComparison.Ordinal));

        public override bool Equals(object? obj) => obj is HookKey other && Equals(other);

        // identity hash:RuntimeHelpers.GetHashCode 走 sync block index,常数时间,不算 string 内容。
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(File), Line);
    }

    public partial class FeatureContext
    {
        // UseMemo 的状态按调用位置(file + line)存。一个调用位置只能对应一份状态,
        // 所以 UseMemo 只能直接写在 OnProject 里(HEVO008 + Debug 运行时检查)。
        internal readonly Dictionary<HookKey, object> _hookPocket = new();
    }

    public static class FeatureContextExtensions
    {
        internal const string UseMemoDocHint = "详见 Hevo.Charting/01.业务Feature设计.md「UseMemo 报错怎么改」";

        private abstract class MemoStateBase
        {
            // 上次命中的帧号(FeatureContext._projectFrame),Debug 下查"同一帧同一 key 被命中两次"。
            public int LastFrame;
        }

        private sealed class MemoState<TVal, TDeps> : MemoStateBase
        {
            public TVal Value = default!;
            public TDeps Deps = default!;
            public bool IsDirty;
        }

        /// <summary>
        /// 帮你省掉"上次的输入 + 上次的结果"两个字段,按依赖缓存一次昂贵计算:<paramref name="dep"/> 跟上次相等就直接返回上次的结果(IsChanged = false),
        /// 不等才调 <paramref name="factory"/> 重算(IsChanged = true,拿它决定这一帧要不要重新发布)。
        /// <para>
        /// 相当于写在代码里的 MultiBinding + IMultiValueConverter(dep 是 Binding,factory 是 Converter,但输入真变了才重跑),
        /// 或者"依赖变了会自己重建的 <see cref="Lazy{T}"/>";
        /// 展开就是 Feature 上两个字段(上次的依赖、上次的结果)加一次比较。
        /// Feature 是长寿对象,状态本来就放字段(所以没有 UseState);只想知道某个值变没变,直接跟字段比,不用 UseMemo。
        /// </para>
        /// <para>
        /// 缓存按调用这一行(文件 + 行号)存,所以只能直接写在 Feature 的 <c>OnProject</c> 里,
        /// 不能写进 helper / 循环 / lambda / 本地函数(编译期 HEVO008,Debug 下运行时也抛)。
        /// dep 用值元组,factory 写成 static lambda,稳态 0 GC。file / line / member 由编译器填,不要手传。
        /// 范例见 Hevo.Charting.Tests/DocSamples/UseMemo/CloseStatsFeature.cs。
        /// </para>
        /// </summary>
        public static (TResult Value, bool IsChanged) UseMemo<TDep, TResult>(
            this FeatureContext ctx,
            TDep dep,
            Func<TDep, TResult> factory,
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0,
            [CallerMemberName] string member = "")
        {
            ctx.AssertProjecting("UseMemo");
            AssertCalledFromOnProject(ctx, file, line, member);

            var key = new HookKey(file, line);
            if (!ctx._hookPocket.TryGetValue(key, out var obj))
            {
                obj = new MemoState<TResult, TDep> { Value = default!, Deps = dep, IsDirty = true };
                ctx._hookPocket[key] = obj;
            }
            var state = (MemoState<TResult, TDep>)obj;
            AssertFirstHitThisFrame(ctx, state, dep, file, line);

            bool changed = false;

            // 依靠 EqualityComparer 查脏，对于 Tuple/Record/ReadOnlyMemory 都是 0-GC
            if (!EqualityComparer<TDep>.Default.Equals(state.Deps, dep))
            {
                state.Deps = dep;
                state.IsDirty = true;
            }

            if (state.IsDirty)
            {
                state.Value = factory(dep);
                changed = true; // 真实发生重算才报 changed
                state.IsDirty = false;
            }

            return (state.Value, changed);
        }

        // 调用方不是 OnProject:要么是 Feature 里的 helper,要么根本不在 Feature 里(provider 等)。
        // OnProject 里的 lambda / 本地函数 [CallerMemberName] 也报 OnProject,那两种由下面的同帧检查兜。
        [System.Diagnostics.Conditional("DEBUG")]
        private static void AssertCalledFromOnProject(FeatureContext ctx, string file, int line, string member)
        {
            if (member == "OnProject") return;

            string feature = ctx._currentFeature?.GetType().Name ?? "<feature>";
            var callerType = FindCallerType();
            string where = $"{System.IO.Path.GetFileName(file)} 第 {line} 行";
            string why = "UseMemo 的缓存按代码行存,不按调用方存:这一行被几处调用,就几处共用一份缓存,互相覆盖,依赖一交替缓存就每次失效。";

            if (callerType != null && !typeof(Feature).IsAssignableFrom(callerType))
            {
                throw new InvalidOperationException(
                    $"[UseMemo 不在 Feature 里] {callerType.Name}.{member} 调了 UseMemo({where},当前帧属于 {feature})。" +
                    $"{why}同一个 {callerType.Name} 类型的多个实例会撞成一份缓存。" +
                    $"改法:缓存改成 {callerType.Name} 自己的字段,例如 `_strategy ??= new XxxStrategy(...)`,依赖变了再重建(参照 TickProvider)。" +
                    UseMemoDocHint);
            }

            throw new InvalidOperationException(
                $"[UseMemo 写在 helper 里] {feature}.{member} 调了 UseMemo({where}),它不是 OnProject。" +
                $"{why}" +
                $"改法:把 UseMemo 移到 OnProject 里,{member} 改成纯函数:`ctx.UseMemo(deps, static d => {member}(d))`。" +
                UseMemoDocHint);
        }

        // 不管调用写在哪,同一 key 一帧命中两次就是撞 key 的直接后果(循环 / 本地函数 / lambda 被调多次)。
        [System.Diagnostics.Conditional("DEBUG")]
        private static void AssertFirstHitThisFrame<TResult, TDep>(FeatureContext ctx, MemoState<TResult, TDep> state, TDep dep, string file, int line)
        {
            if (state.LastFrame != ctx._projectFrame)
            {
                state.LastFrame = ctx._projectFrame;
                return;
            }

            // 只在抛错路径上 ToString。
            string feature = ctx._currentFeature?.GetType().Name ?? "<feature>";
            throw new InvalidOperationException(
                $"[UseMemo 同一帧执行了两次] {feature} 的 UseMemo({System.IO.Path.GetFileName(file)} 第 {line} 行)这一帧被执行了两次:" +
                $"第一次依赖 = {Describe(state.Deps)},第二次依赖 = {Describe(dep)}。" +
                "通常是写在循环、本地函数或 lambda 里。UseMemo 的缓存按代码行存,同一行执行几次就共用一份缓存,互相覆盖,缓存每次失效。" +
                "改法:对整个集合调一次 UseMemo、factory 里一次算完返回数组;或者每项各自缓存在字段数组里(_cache[i])。" +
                UseMemoDocHint);
        }

        private static string Describe<T>(T value) => value?.ToString() ?? "null";

        // 抛错路径专用:沿调用栈找到 UseMemo 的调用方所在类型(lambda / 本地函数编译成嵌套类型,往外层找)。
        private static Type? FindCallerType()
        {
            var frames = new System.Diagnostics.StackTrace(false).GetFrames();
            foreach (var frame in frames)
            {
                var type = frame.GetMethod()?.DeclaringType;
                if (type == null || type == typeof(FeatureContextExtensions)) continue;
                while (type.DeclaringType != null && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    type = type.DeclaringType;
                return type;
            }
            return null;
        }

        public static HevoRect GetPlotArea(this RenderContext ctx)
        {
            var areaTrait = ctx.Shared().Read<PlotAreaTrait>();
            if (areaTrait != null && areaTrait.Area.Width > 0 && areaTrait.Area.Height > 0)
            {
                return areaTrait.Area;
            }

            var vpTrait = ctx.Shared().Read<ViewportSizeTrait>();
            return vpTrait != null ? new HevoRect(0, 0, (float)vpTrait.Width, (float)vpTrait.Height) : HevoRect.Empty;
        }
    }
}
