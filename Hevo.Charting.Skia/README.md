# Charting.Skia

Charting 的 Skia 上屏后端，**WPF 专用**：合成面是 WPF 元素（`WriteableBitmap` / `SKGLElement`），挂在 `ChartCell` 里。图表的 Feature 投影、图层录制、DrawCmd 缓冲都不变，只把"指令 → 像素"这一步从 WPF `DrawingVisual` 换成 Skia。

核心库 `Hevo.Charting` 仍然零第三方依赖；SkiaSharp 只在这个程序集里。

> **状态：实验性，暂不引入产品。** 代码留在仓库里随测试编译，但不进任何打包产物（`IsPackable=false`），业务工程（含 LowCodeDemo）不引用本程序集。要试用，在自己的工程里加 `ProjectReference` 后按下文挂到 `ChartCell.RenderBackend`。

## 用法

```csharp
// 单张图:后端交给 cell,之后由 cell 负责释放
cell.RenderBackend = new SkiaRenderBackend();                       // CPU(默认)
cell.RenderBackend = new SkiaRenderBackend(SkiaRenderTarget.Gpu);   // OpenGL;换掉时旧后端自动 Detach + Dispose

// 全局:在创建任何 ChartCell 之前设置一次
ChartCell.DefaultRenderBackendFactory = () => new SkiaRenderBackend();

// 切回 WPF:当前后端被 Detach + Dispose,不要再自己 Dispose
cell.RenderBackend = null;

// cell 不再使用时:Shutdown 同样会 Detach + Dispose 后端
cell.Shutdown();
```

所有权规则：

- 挂到 `ChartCell.RenderBackend` 的后端归这个 cell 所有。替换、置空、`Shutdown()` 时，cell 先 `Detach()` 再 `Dispose()`。
- cell 卸载（`Unloaded`，例如切 Tab）只是暂停，不释放后端，重新加载后继续用。
- 一个后端实例只能挂在一个 cell 上；挂到第二个 cell 会抛 `InvalidOperationException`。已释放的实例不能再挂。

运行时随时可切，下一帧生效，不重算 Feature、不重录图层。和 WPF 后端的效果 / 性能对比用 render-probe / latency-probe 的 `--renderer=skia|skia-gpu`（见下文）。

主题切换后调用 `SkiaRenderBackend.InvalidateResources()`：资源笔刷会按新主题重新解析，所有图层在下一帧重录（方法内部会请求这一帧）。

## 挂上后端后不再生效的设置

后端只消费图层的 DrawCmd 指令（`LayerBuffer.Drawing`），WPF 路径上的这些开关对它不起作用：

- `ChartLayer.Mode = RenderMode.Bitmap` 与 `ChartCell.RenderModeOverride`：位图模式是 WPF 路径的光栅化策略，挂上 Skia 后所有图层都由 Skia 光栅化；
- `ChartCell.StaticCachePolicy`：作用在 WPF 静态画布的 `BitmapCache` 上，挂上后端后静态画布已折叠；
- 图层的 `BitmapBuffer`（`LayerBuffer.Bitmap`，业务预栅格化好的像素）会被忽略，不会上屏。

切回 WPF（`RenderBackend = null`）后这些设置恢复生效。

## 工作方式

- **失效粒度是图层**，与 WPF `DrawingVisual` 后端一致。每个图层一张 `SKPicture`，只有变脏的图层才重录；后端不对图层内部的区域做推算。
- **两块合成面**：静态层（网格、轴、序列）一块，交互层（十字光标、K 线最新一根）一块，分别对应 WPF 路径的静态画布和交互画布。
  - 哪块面上有图层重录，就把那块整面清掉、按 Z 序回放该面的所有 picture。没变的面不动。
  - 所以十字光标移动只重画交互面，tick 只重录 K 线动态层、只重画交互面。
  - 想进一步省开销，做法是继续拆图层，而不是在后端里抠像素。
- **按设备像素录制**：picture 直接按物理像素录，像素对齐（PixelSnap 规则）在设备像素上做，整数线宽换算成整数设备像素，所以 125% / 150% 缩放下 1px 线仍然只占 1 个设备像素。合成面 1:1 回放，不再缩放。DPI 变化（跨屏拖动、改系统缩放）时所有图层重录。
- **CPU 目标**：`SKSurface` 直接包住 `WriteableBitmap` 的后台缓冲，零拷贝。光栅在 `Present` 里同步完成，然后整面 `AddDirtyRect`。
- **GPU 目标**：使用 `SKGLElement`（OpenTK GLWpfControl，GL → D3DImage 互操作），背景透明。
  - 只有一块面，两组图层按 Z 序画在同一块上，因为两块 GL 面叠放时上层不透明，会盖住下层。
  - 任一组有变化就整面重画。
  - 需要 GL 3.3 以上。创建失败、打不开透明背景、或者首次绘制时 `GRContext` 建不出来 / GL 调用抛异常，都会记一条 `Trace` 警告并自动回退到 CPU 面（整面重画），可从 `ActiveTarget` 查看实际生效的目标。

## 画面口径

和 `WpfDrawingRenderer`（WPF 默认后端）一致的部分：

- 同样的像素对齐规则、同样的相邻 `DrawLine` 合批、`DrawRectangles` 逐个画，矩形填充关闭抗锯齿（边缘已对齐到设备像素，结果跟 WPF 的抗锯齿 + 设备像素对齐一致）；
- Pen 的语义：`HevoPen.LineCap` 按 WPF `PenLineCap` 解释（0 Flat / 1 Square / 2 Round；3 Triangle Skia 没有，退化为平头），`MiterLimit` = 10，虚线长度以线宽为单位、段端按 WPF 默认的 `DashCap = Square` 外扩半个线宽；
- 渐变：`HevoLinearGradientBrush` 与资源里的 `LinearGradientBrush` 按 `MappingMode` 处理，默认 `RelativeToBoundingBox` 时起止点按图元包围盒换算；资源笔刷的 `Opacity` 计入颜色；
- 路径标记：支持 WPF 的 `F0` / `F1` 填充规则前缀，没有前缀时默认 EvenOdd，与 `Geometry.Parse` 相同；
- 默认字体 Arial；主字体缺字形（如中文）时按字符做字体回退，不会出现豆腐块。

已知差异：

- 斜线、曲线、圆的抗锯齿由 Skia 计算，边缘像素的覆盖率与 WPF 不完全相同；
- 文字由 Skia 排版和光栅（字形 hinting、亚像素定位、回退字体的选择都可能与 WPF `FormattedText` 不同），宽度可能有零点几像素的差别；
- Triangle 线端画成平头；虚线的线首 / 线尾端按平头处理（中间各段的段端按 Square）；
- 径向等其它渐变只取首个色标当纯色；渐变画刷的 `Transform` / `RelativeTransform` 不支持；
- `DrawVideo` 不支持（该命令目前没有生产者）。

与位图后端（`RenderMode.Bitmap`，像素中心采样、无抗锯齿的参照实现）相比，水平 / 垂直线、矩形和线端、虚线的覆盖基本相同，差异主要在文字和斜线的抗锯齿上。

## 诊断

`SkiaRenderBackend` 暴露上一帧的以下指标：

- `LastRecordTicks`：重录 picture 耗时
- `LastRasterTicks`：CPU 光栅耗时
- `LastGpuPaintTicks`：GPU 绘制耗时
- `LastRecordedLayers`：重录的图层数
- `LastRepaintedSurfaces`：重画的合成面数

## 性能对照

用 render-probe 跑以下几种模式：

- `skia`：CPU
- `skia-gpu`：GPU

```bash
dotnet run -c Release --project Hevo.Charting.Benchmarks -- --render-probe --modes=inc,bmp,skia,skia-gpu --scenarios=HoverNoTip,Pan,Zoom,Tick,Append
```

报告末尾的 Skia 表把帧耗时拆成录制和光栅两部分。注意口径：

- WPF 模式的帧耗时不含 WPF 渲染线程的光栅化；
- Skia CPU 模式的帧耗时含光栅化；
- Skia GPU 模式的光栅发生在随后的 WPF 渲染里，单独计算。

要比较端到端延迟（含合成上屏），用 latency-probe：

```bash
dotnet run -c Release --project Hevo.Charting.Benchmarks -- --latency-probe --renderer=skia
```

设置环境变量 `HEVO_PROBE_PNG_DIR=目录` 后，render-probe 会把每个测量段的第一张图存成 PNG，用来比对各后端的画面。

## 已知限制

- .NET 5：本程序集目标是 net10（`net10.0-windows10.0.19041.0`），不在 [README §2](../Hevo.Charting/README.md) 必编清单里；GPU 依赖（SkiaSharp.Views.WPF / OpenTK.GLWpfControl）在 .NET 5 下不可用。核心库 `Hevo.Charting` 里与后端相关的代码仍保持 .NET 5 可编译。
- 只支持 WPF：合成面是 WPF 元素，不能用于其它 UI 框架。
- 控件层（Tooltip 等 Widget）仍是 WPF 控件，不经过 Skia。
