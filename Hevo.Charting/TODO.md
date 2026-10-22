# Charting TODO

性能优化、架构清理、待还的技术债。每条都是触发条件 + 改动方案 + 触发后的实施步骤。

---

## ✅ 已完成:Skia 引用移除（commit 8350acf）

> 现状补充:本节记录的是从**核心库**移除旧 `SkiaRenderer` 一事,核心库 `Hevo.Charting` 至今仍零第三方依赖。此后核心库新增了 `IChartRenderBackend` 整 cell 替换扩展点(`ChartCell.RenderBackend`),它与 [Renderer.md](Renderer.md) 介绍的按图层混合思路(`IRendererProvider`)是两条并存的路线;WPF 下的 Skia 实现(`SkiaRenderBackend`)后来也删了,Skia 只给 Avalonia 宿主用(`Hevo.Charting.Skia`)。

**触发原因**:Skia 路径在全屏 + 特定 canvas size 下出现 ±1 列错位 bug（SkiaSharp.Views.WPF 多个图层栅格化路径都没法绕开）。已经全员切到 WPF Software 并且通过 `_drawingCanvas.CacheMode = BitmapCache` 拿回了 perf,Skia 引用整体可拔。

**改动清单(全部完成)**:
- ✅ 删 `Renderers/SkiaRenderer.cs` / `Renderers/SkiaRasterRenderer.cs`
- ✅ `Renderers/RenderProvider.cs` 去 `SkiaRenderProvider`
- ✅ `Core/ChartCell.cs` 去 `_skiaElement` / `_skiaProvider` / `OnSkiaPaintSurface` / `_hardwareLayers` 全部相关引用
- ✅ `Hevo.Charting.csproj` 去 `SkiaSharp` / `SkiaSharp.Views.WPF` PackageReference
- ✅ `IDrawingSink.PushPixelSnapping` + `DrawOp.PushGuidelineSet` 一并删除(本就是 Skia 时代的 hack)

**API 兼容**:`RenderMode.Hardware` 枚举值保留(避免下游枚举值消失编译错),但路由到 Software。详见 [06.WPF 渲染规约.md](06.WPF%20渲染规约.md) §1 / §15。

---

## ⏳ 待触发:动态 CandleLayer 路由到独立 host

**触发条件**(任意一条):
- 心跳/tick 频率 ≥ 5 Hz
- BitmapCache 重建单次 ≥ 5ms
- hover 期间出现节奏跟心跳一致的可见卡顿

**背景**:[CandleFeature.cs:107-109](Features/CandleFeature.cs:107) 已经做了冷热分离 (`_staticLayer` + `_dynamicLayer`),但两个都进 `_drawingCanvas`(Level=Main)。`_drawingCanvas` 挂了 BitmapCache → dynamic 任何改动都让缓存整体失效 → 历史 9999 根蜡烛重新栅格化。冷热分离的红利吃不到。

**方案 A(推荐,5 行)**:
```csharp
// CandleFeature.cs
private readonly CandleLayer _dynamicLayer = new() { Level = ChartLayerType.Interaction };
```
ChartCell 路由器自动把它送进 `_overlayCanvas`(无 cache,跟 Crosshair / Tooltip 同 host)。心跳改动不污染 `_drawingCanvas` 缓存。

**潜在副作用**:dynamic K 线 z-order 跟 Crosshair 同层。如果业务希望 Crosshair 必须盖住 K 线,需要调整 `_overlayVisualRegistry` 插入顺序,或走方案 B。

**方案 B(架构纯净,~50 行)**:加第三个 canvas `_hotCanvas` + `ChartLayerType.Hot` 新枚举值。z-order 干净。
- ChartCell 加 `_hotCanvas` + `_hotVisualRegistry`
- `ChartLayerType` 新增 `Hot = 15`(在 Main 之后、Selection 之前)
- 路由器扩展为四分支:Hardware / Hot / Interaction / 其它
- CandleFeature 改 `_dynamicLayer.Level = Hot`

实现量是 A 的 ~10 倍。先跑 A,不够再升 B。

**验证方法**:跟 `[FrameGap]` 同款 stopwatch,只在 `_drawingCanvas.CacheMode != null` 重建时打印,对比改动前后 gap。

---

## ⏳ 待触发:Crosshair 延迟进一步压低

**触发条件**:用户反馈 hover 仍有微感延迟(目前已经做过 Dispatcher.InvokeAsync 跳过的 sync 优化)。

**当前架构**:
- mouse move (UI 线程) → board write → RequestUpdate → 同步订阅 `CompositionTarget.Rendering` → 下一帧 vsync 跑管线 → WPF 上屏 (vsync+1 帧)
- 总延迟 ≈ 1-2 vsync(16-32ms)

**进一步优化方向**:
1. **同步直渲**:mouse move handler 内,在写完 PointerHitPort 之后,直接同步 `ExecutePipeline + Invalidate`,跳过 `CompositionTarget.Rendering` 这一跳。需要解决:管线在 board write 锁内被同步调用的可重入问题。
2. **Crosshair 走 Skia**(如果将来 Skia 回归):crosshair 是 thin line,不会触发 grid ±1 列 bug。Skia bitmap 隔离 + GPU 路径,延迟和帧率都能拿到最优。
3. **自定义 UIElement 替代 DrawingVisual**:Override `OnRender(DrawingContext)` 自己控制 invalidation 粒度,缩 dirty bbox 到 crosshair 实际像素带宽。

---

## ✅ 已处理:静态层缓存策略

`StaticCachePolicy` 只留 `On / Off`,默认 On;`Adaptive`、滞回阈值 500 / 200、`ChartCell.ReportVisibleDataCount` 已删除。

- 阈值没有测量依据,代码里也没有任何地方用 `Adaptive`(默认一直是 On,阈值从未生效)。
- 默认 On 的依据(latency-probe,集显 GPU,120 Hz,每张图可见 1000 根 + 成交量):4 张图同屏时十字光标端到端
  开缓存 16.6 ms、关缓存 33.2 ms(多等一帧),缩放持平;单图、可见 120 根时开关一致。这也是 4 月加缓存时的初衷
  (多图、K 线多时十字光标稳在一帧)。
- WPF 软件渲染(远程桌面同款路径)下缓存反而更慢:可见 120 根十字光标 / 缩放 18 → 33 ms,可见 1000 根缩放 33 → 50 ms。
  业务在这类环境可设 Off。将来若要自动切换,依据应是渲染层级(`RenderCapability.Tier`,远程桌面连上 / 断开会变),
  不是可见根数。
- 开缓存时画面与不开逐像素一致、静态层跟十字线同列:全图抗锯齿,横竖内容按设备像素对齐,静态画布原点平移到设备像素(06 号文档 §4.3 / §14.9)。
  只在 100% DPI、软件合成(RenderTier 0)上实测;125% / 150% 真机、GPU 合成待测。
- 未测:弱独显 / 4K / 多屏混合 DPI 的真机上屏;4 图结果只测了一轮。

---

## ⏳ 待还的技术债

### ✅ 已完成:Grid pixel asymmetry workaround
1.5px 妥协是 Skia ±1 列错位 bug 的临时方案。Skia 拔除 + §14 vertex snap + AA on 路径已能正常处理 1px H/V 线,业务侧(分时图基类,本仓库未收录)已恢复 `LineStyle.FromResource(BrushKeys.L1, 1.0)`。`GridStyleTrait.Create / FromResource` 默认值本来就是 `thickness=1.0`,无需进一步改动。

### `_overlayVisualRegistry` 插入排序
当前 `AddUnmanagedLayer` 对 `_overlayVisualRegistry` 走 Level 升序,但 overlay 内只有 Crosshair 和 TooltipWidget 都是 Interaction(同 Level),实际 Z-order 由插入顺序决定。如果将来动态 CandleLayer 也进 overlay(方案 A),要明确 Z 序规则。

### `dc.DrawRectangle` 逐 rect vs 批量
[DrawingRenderer.cs](Renderers/DrawingRenderer.cs) 的 `DrawRectangles` 当前逐 rect 调用,是为了规避 StreamGeometry 多 figure + isClosed=true 在 WPF 下的红色幻线伪影。如果将来发现批量是性能瓶颈,可探索 `GeometryGroup` of `RectangleGeometry`(每个 rect 独立 Geometry,绕开 figure 间幻线)的批量方案。

### 移动端 / 跨平台
当前 WPF-only,没有 Avalonia / MAUI 路径。如果需要跨平台,框架的 `IRenderer<TBuffer, TContext>` 接口设计支持新增 backend,但需要为每个 platform 实现 RasterRenderer + DrawingRenderer。
