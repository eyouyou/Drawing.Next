# Hevo.Charting.Avalonia

图表引擎的 Avalonia 宿主,渲染直接用 Skia。Feature / Layer / Schema 跟 WPF 完全共用,schema 不改一行就能挂到 Avalonia 上。

| 工程 | 内容 |
|---|---|
| `Hevo.Charting.Avalonia` | `AvaloniaChartCell` 控件、联动看板 `AvaloniaLinkedChartDashboard`、顶部信息栏 `AvaloniaUniversalHeaderFeature`、指针适配器(含触屏)、Skia 资源解析、悬浮窗图层、外壳装饰接口;蓝图 / dashboard 启动器、蓝图编辑器;Debug 下的调试菜单与拓扑监控 |
| `Hevo.Charting.Avalonia.Demo` | 三页:① 手写 schema 的联动看板(K 线 + SMA20 + 成交量,顶部信息栏、十字线联动、悬浮窗、平移 / 滚轮 / 触屏缩放、分隔条调高度);② 同样内容的蓝图 JSON 起的看板;③ 打开这份 JSON 的蓝图编辑器。schema、数据源、蓝图跟 WPF 示例是同一份(`Hevo.Charting.Samples`) |
| `Hevo.Charting.Avalonia.Tests` | Avalonia.Headless + 真 Skia 的无窗口测试 |

版本跟接入方 FuturesPC 一致:net10.0,Avalonia **12.0.4**,SkiaSharp 不直接引用、用 Avalonia.Skia 带进来的 **3.119.4**
(`Hevo.Charting.Skia` 钉同一版本:一个进程里只能有一份 Avalonia / SkiaSharp)。
本工程不受 .NET 5 红线约束,不进 `scripts/check-net5.ps1`。

## 用法

```csharp
var cell = new AvaloniaChartCell { Template = new MySchema(dataSource) };
window.Content = cell;
```

跟 WPF 的 `new ChartCell { Template = schema }` 一样:挂进可视树时装配,换 `Template` 时旧 schema 先卸载;不再使用时调 `cell.Shutdown()`。示例见 `Hevo.Charting.Avalonia.Demo/DemoKLineSchema.cs`。

运行示例:

```bash
dotnet run --project Hevo.Charting.Avalonia.Demo
```

加 `-- --blueprint` / `-- --designer` 打开就停在蓝图看板 / 蓝图编辑器页。

## 怎么接上核心的

`AvaloniaChartCell` 实现 `IChartCell`(给 Feature)和核心的 `IChartCellHost`,持有一个 `ChartEngine`:

| 宿主职责 | Avalonia 实现 |
|---|---|
| 每帧回调(按需挂、静置不挂) | `TopLevel.RequestAnimationFrame`,一次性回调;帧内回流的 `RequestUpdate` 会再挂下一帧 |
| 图层上屏对象 | `ChartLayer.HostVisual` = `AvaloniaLayerVisual`,存该层前台指令用 `SkiaChartRenderer` 录成的 `SKPicture`(带引用计数) |
| 上屏 | 脏图层重录 picture;静态层 / 交互层各一块 `LayerSurface`,哪组有重录就只重画那块(十字线只动交互面) |
| 回放 | `ICustomDrawOperation` 里经 `ISkiaSharpApiLeaseFeature` 拿 `SKCanvas`,画布变换换成只平移(取整到设备像素),picture 按设备像素录制 |
| 尺寸 / DPI | 内容面板 `SizeChanged` 发 `ViewportSizeTrait` + `InvalidateEnvironment`(亚像素抖动不触发);`TopLevel.RenderScaling` 写进 `PixelsPerDip`,变了所有图层重录 |
| 暂停 / 恢复 | `Loaded` / `Unloaded`、`IsVisible`、窗口最小化 → schema `Resume` / `Suspend` |
| 输入 | `AvaloniaPointerAdapter`:隧道事件给路由器做区域认领,冒泡事件给行为链,语义同 `WpfPointerAdapter`;触屏见下 |
| 控件类图层 | 控件缓冲(`WidgetBuffer`)里的 Avalonia 控件摆进控件层 Canvas;悬浮窗 = `AvaloniaTooltipLayer` |
| 外壳装饰 | 切面实现 `IAvaloniaDecorator`,沿 Aspect 链从内到外包 |
| 资源 | `AvaloniaSkiaResources`:资源笔刷 / 字体按当前主题查 Application 资源,图片认 Avalonia `Bitmap`;主题切换后让 schema 环境失效(`IFeatureProjector.InvalidateEnvironment()`),资源笔刷 / 字体按新主题重新解析、全部图层重画 |

| 平台实现登记 | `AvaloniaChartHost.Services`(本宿主的一份 `ChartHostServices`):悬浮窗图层、头部、`HevoResourceString` 解析、蓝图名 `"UniversalHeaderFeature"` 的别名、调试菜单,程序集加载时由 `[ModuleInitializer]` 登记 |

登记是按宿主分开的:WPF 宿主登记它自己那份(`WpfChartHost.Services`),核心按 cell 取,所以 **WPF 与 Avalonia 可以在同一进程里共存**
(比如 WPF 程序里嵌一个 Avalonia 窗口),各自的 cell 用各自的悬浮窗、头部、资源字典。细节见 `docs/变更说明-核心拆分-2026-10.md`「宿主平台实现按宿主区分」。

## 联动看板与顶部信息栏

```csharp
var dashboard = new AvaloniaLinkedChartDashboard(new LinkedChartContext())
    .AddMaster(new KLineMainSchema(ds), heightRatio: 3)
    .AddPane(new KLineVolumeSchema(ds), heightRatio: 1);
dashboard.IsPaneResizeEnabled = true;   // pane 之间放分隔条(默认关)
```

API 跟 WPF 的 `LinkedChartDashboard` 一致(`AddMaster` / `AddPane` / `AddRaw`、运行期增删副图、`GetPaneRatios` / `TrySetPaneRatios` / `ResetPaneRatios` / `PaneRatiosChanged`)。
联动本身(主图 / 副图上下文注入、端口镜像)在核心的 `SchemaContext` / `LinkedChartContext`,两个宿主共用。

顶部信息栏:schema 里照旧写 `env.SetupUniversalHeader(hitPort)`。这个扩展方法在核心,按宿主注册的工厂建头部 ——
WPF 下是 `UniversalHeaderFeature`(`IWpfDecorator`),Avalonia 下是 `AvaloniaUniversalHeaderFeature`(`IAvaloniaDecorator`)。
取哪根、收哪些字段、格式化都在核心基类 `UniversalHeaderFeatureBase`,宿主子类只管控件。蓝图 JSON 里的 `"UniversalHeaderFeature"` 在 Avalonia 下解析到 Avalonia 实现。

## 触屏

对应 WPF 的 Manipulation 通道,换算成同样的 `ManipulationStarting` / `ManipulationDelta` 交给行为链(`TouchBehavior`),所以语义一致:

- 一次触摸序列(第一根手指按下到最后一根抬起)开头问一次行为链要不要接管。开了平移 / 缩放(`ChartInteractionMode.Pan` / `Zoom`)就接管:
  单指横移 = 平移;双指捏合 = 以两指中点为中心缩放(比例 = 两指间距之比);手指数变化时换新基准,视图不跳。
- 不接管时主触点当鼠标(悬停出十字线等),其余手指忽略 —— 同 WPF 把主触点提升成鼠标。
- 触屏抬手后没有惯性(WPF 的 Manipulation 路径也没有;鼠标拖动的惯性 `InertiaPan` 两边都有)。
- 用的是原始多指针事件,不是 Avalonia 的 `PinchGestureRecognizer`:识别器的缩放是累计值、没有平移分量,跟 WPF 的增量模型对不上。

## 蓝图 JSON 起看板 / 单图

```csharp
SampleDashboards.RegisterComponents();   // 或:GraphViewerBootstrap.Initialize() + 登记业务数据源
var result = AvaloniaDashboardLauncher.LaunchFromJson(json);   // 也有 LaunchEx(Dashboard) / DryRun
if (result.Dashboard != null) window.Content = result.Dashboard;

AvaloniaBlueprintLauncher.LaunchEx(blueprint, ownerWindow);    // 单张蓝图弹预览窗;CreateCell(...) 只建 cell 不弹窗
```

校验(`BlueprintPreflight.DryRun`)、render-leaf 数据源的实例化 / 上下文注入、逐 cell 装配、全看板 loading 同步翻牌都在核心
(`BlueprintPreflight` / `DashboardAssembler`),跟 WPF 的 `BlueprintLauncher` / `DashboardLauncher` 是同一份代码;Avalonia 版只给看板控件和窗口。
`DashboardLaunchOptions`(per-cell 上下文、configure 回调、handler、预构造数据源)两边通用。

## 蓝图编辑器

`AvaloniaBlueprintDesigner`(一个控件,放进任意窗口):打开蓝图 / dashboard JSON(dashboard 挑一个 cell 编辑)、看图、拖节点(带对齐吸附)、
从输出端口拖到输入端口连线、点输入端口断开、框选、Delete 删节点、Ctrl/Cmd+Z / Y 撤销重做、Home 适配视图、右键拖 / 滚轮平移缩放、
minimap 跳转、自动布局、保存 JSON、"运行"弹预览图。图模型、序列化、图层、交互逻辑(`GraphInteractionFeatureBase`)都在核心,跟 WPF 编辑器共用;
Avalonia 版只有事件接线(`AvaloniaGraphInteractionFeature`)和工具栏。

## 调试菜单(Debug 构建)

跟 WPF 一样,schema 装配后 cell 的右键菜单里有"开启拓扑水管监控器",换 schema 时成对摘掉;schema 子类的 `InjectDebugMenus` 也会调
(往 `((AvaloniaChartCell)chart).ContextMenu` 加项)。监控窗(`AvaloniaTopologyMonitor`)画数据源 → 端口 → Feature → 图层的流向图
(布局用核心的分层布局,线宽按命中次数,点节点高亮上下游、底栏显示最近值 / OnProject 耗时),下面是关键事件时间线,每 500ms 刷新。

## 跟 WPF 宿主的差别

- **只有 Skia 一条渲染路,这是设计选择**:没有 WPF 那套位图模式(`RenderMode.Bitmap`)、`RenderBackend` 切换和 `StaticCachePolicy`。
  WPF 要这些是因为 WPF 自己的矢量合成有 BitmapCache 偏像素、批量矩形伪影等问题,得有退路;Avalonia 本身就用 Skia 合成,
  图层直接录成 `SKPicture`、静态面 / 交互面分开回放,已经是 WPF 那几种模式想达到的效果(静态层不跟十字线一起重画、像素对齐),
  再做一套位图后端只会多一份要维护的渲染路径。
- Avalonia 按下时会把指针隐式捕获到命中的元素,适配器把"捕获在图表自己的面板上"当成没被别人抓着(WPF 没有隐式捕获)。
- 外壳 Feature 是宿主控件:Avalonia 下要实现 `IAvaloniaDecorator`(WPF 是 `IWpfDecorator`)。要一份 schema 两边都出外壳,
  按"核心基类 + 两个宿主子类 + `RegisterFeature` 登记 + schema 里 `AddHostFeature<基类>()`"写,见 `docs/变更说明-核心拆分-2026-10.md`「双宿主外壳 Feature」。
- 蓝图编辑器还缺:节点属性编辑窗(WPF `NodeEditorWindow`,双击节点这里只在状态栏显示节点信息、抛 `NodeEditRequested`)、
  组件选择器(往画布加新节点)、handler 选择器、dashboard 多 cell 工作区(`DashboardWorkspace`);画布上的双指捏合没接(用滚轮)。
- 拓扑监控窗比 WPF 版简单:没有端口折叠、Ctrl+E 单帧流向捕获动画、拖动钉住节点、内存迷你图。

## 测试

```bash
dotnet test Hevo.Charting.Avalonia.Tests
```

覆盖:装配后图层录出 picture、真 Skia 出图有内容、静置不挂帧回调、悬停出命中且十字线只进交互面、拖动平移、滚轮缩放、换 Template 释放图层和 picture、悬浮窗图层注册;联动看板共享视口、副图拖动带动主图、主图悬停命中同步到副图、头部默认显示末根 / 悬停显示命中那根、分隔条比例、副图摘掉视口管家和悬浮窗;
触屏单指平移 / 双指捏合缩放 / 不接管时当鼠标;调试菜单注入与换 schema 摘除、拓扑监控出节点和图层;蓝图 JSON 起看板并用 Avalonia 头部;`AddHostFeature` 建本宿主子类并包外壳;资源文本查 Avalonia 资源;
蓝图编辑器打开 dashboard、拖节点 + 撤销、断开再连回连线、运行。

**为什么不并进 `Hevo.Charting.Tests`**:宿主槽位已经按宿主分开,两种宿主能同进程共存(`Hevo.Charting.Tests/HostCoexistenceTests` 就把 Avalonia 宿主加载进 WPF 测试进程验证这点),
不再是并不了的原因。仍然分开是测试基础设施不兼容:Avalonia.Headless 要整个测试程序集一个 Avalonia 应用(`[assembly: AvaloniaTestApplication]`)、
用例跑在它的专用 UI 线程上(`[AvaloniaFact]`),这个运行器编在 xunit v3 上;WPF 测试是 xunit v2 + 每个用例自起 STA 线程 + net10.0-windows。
硬并要么把 1200 多个 WPF 用例迁到 xunit v3,要么两套 UI 线程模型在一个进程里抢 Application 单例,得不偿失。
