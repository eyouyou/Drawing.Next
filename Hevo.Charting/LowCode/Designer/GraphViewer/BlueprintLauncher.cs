using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 把 GraphViewer 当前画布编译出来的 ChartBlueprint 跑起来。
    /// 反射 BlueprintRunner.Run&lt;TItem&gt; 泛型调用,弹一个独立窗口承载 ChartCell。
    /// 调用方失败时不会抛——返回错误字符串供 UI 显示。
    ///
    /// <para>
    /// <b>关于运行时上下文注入</b>:蓝图模型只描述拓扑(类型 / 属性 / 端口),
    /// 描述不出"业务上下文"(典型:<c>TimeShareDataSource.LoadAsync(security)</c> 或 <c>Context = ...</c>)。
    /// 通过 <paramref name="configureDataSource"/> 回调,在 BlueprintRunner 执行**之前**给数据源注入运行时上下文,
    /// 业务侧可以为每种数据源准备一个 demo loader (例如:if (ds is TimeShareDataSource t) await t.LoadAsync(...))。
    /// 不传该回调则数据源保持初始空状态,LogicalLength=0 → ViewportManager 会写 invalid range → 整个图表
    /// 不渲染任何数据,仅看到底色(俗称"黑屏")。
    /// </para>
    /// </summary>
    public static class BlueprintLauncher
    {
        /// <summary>
        /// 启动并返回错误描述(成功时返回 null)。失败时弹窗 owner 用作父级。
        /// <para>
        /// 注入数据源运行时上下文有两条路,任选其一即可:
        /// <list type="bullet">
        ///   <item><b><paramref name="dataSourceContext"/></b>:类型化便捷参数。Launcher 反射查 DataSource 上是否有
        ///         <c>LoadAsync(TContext)</c> 方法(<see cref="ReactiveDataSource{TSource, TContext, TItem}"/> 派生类标配),
        ///         参数类型可赋值 → 自动调用,fire-and-forget。</item>
        ///   <item><b><paramref name="configureDataSource"/></b>:万能回调。<paramref name="dataSourceContext"/> 应用之后
        ///         同步执行,业务可在此自由处理(MinuteCeiling 配置 / 多步 Load 链 / 自定义 SwitchContext 等)。</item>
        /// </list>
        /// 两个参数同时提供时,先按类型化参数走 LoadAsync,再跑回调。
        /// </para>
        /// </summary>
        public static string? Launch(
            ChartBlueprint blueprint,
            Window? owner = null,
            object? dataSourceContext = null,
            Action<object>? configureDataSource = null,
            BlueprintHandlerRegistry? handlers = null)
            => LaunchEx(blueprint, owner, dataSourceContext, configureDataSource, handlers).Error;

        /// <summary>
        /// 结构化版 Launch:返回 <see cref="BlueprintLaunchResult"/>,既有 Error(致命错误,启动被中断)
        /// 也有 Diagnostics(警告,启动了但有连接被 skip)。UI 侧可用诊断列表把"加载成功但黑屏"
        /// 这种隐性故障变成显式提醒,调试时间从分钟级降到秒级。
        /// <para>
        /// <paramref name="handlers"/>(协议扩展 §K):蓝图含 <see cref="ChartBlueprint.Triggers"/> 或
        /// Feature Properties 里有 Delegate 字段(如 OnRequireDataAsync / FutureXLabelFormatter)时,
        /// 业务侧通过 <see cref="BlueprintHandlerRegistry.RegisterFetch"/> /
        /// <see cref="BlueprintHandlerRegistry.RegisterDelegate"/> 注册命名委托,蓝图侧用 string 名字引用。
        /// </para>
        /// </summary>
        public static BlueprintLaunchResult LaunchEx(
            ChartBlueprint blueprint,
            Window? owner = null,
            object? dataSourceContext = null,
            Action<object>? configureDataSource = null,
            BlueprintHandlerRegistry? handlers = null)
        {
            // 先 dry-run 收诊断;致命错误直接返回,警告随启动结果带回。
            var diag = DryRun(blueprint, handlers);
            if (diag.Error != null) return diag;

            var startError = LaunchInternal(blueprint, owner, dataSourceContext, configureDataSource, handlers);
            return new BlueprintLaunchResult { Error = startError, Diagnostics = diag.Diagnostics };
        }

        /// <summary>
        /// 不实际启动,只对蓝图做静态校验。返回致命错误 + 警告诊断。校验本身在核心 <see cref="BlueprintPreflight.DryRun"/>(各宿主共用)。
        /// </summary>
        public static BlueprintLaunchResult DryRun(ChartBlueprint blueprint, BlueprintHandlerRegistry? handlers = null)
            => BlueprintPreflight.DryRun(blueprint, handlers);

        private static string? LaunchInternal(
            ChartBlueprint blueprint,
            Window? owner,
            object? dataSourceContext,
            Action<object>? configureDataSource,
            BlueprintHandlerRegistry? handlers)
        {
            // 1-2. 找 render-leaf、实例化、注入上下文(平台无关,在核心)
            var prepError = BlueprintPreflight.PrepareLeafDataSource(blueprint, dataSourceContext, configureDataSource,
                out var leafId, out var leafDsType, out var leafDsInstance);
            if (prepError != null) return prepError;

            // 3. 拉起预览窗口
            var cell = new ChartCell { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1B, 0x1F)) };
            var win = new Window
            {
                Title = $"蓝图预览 — {leafDsType.Name}",
                Width = 1024, Height = 600,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = cell,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1A)),
            };

            // 4. 装配 schema 到 cell。leaf 实例显式塞 instances;其它 DS(Cascade 上游 stocklist 等)
            //    交给 BlueprintRunner 自动 ComponentRegistry.Resolve + new + InjectProperties。
            try
            {
                var instances = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [leafId] = leafDsInstance,
                };
                BlueprintRunner.Run(cell, blueprint, instances, handlers);
            }
            catch (Exception ex) { return $"RunBlueprint 抛异常:{(ex.InnerException ?? ex).Message}"; }

            // 8. 窗口关闭时回收 ChartCell 状态
            win.Closed += (_, __) => cell.Shutdown();
            win.Show();
            return null;
        }
    }
}
