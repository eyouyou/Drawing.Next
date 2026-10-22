# Charting.Skia

DrawCmd → `SKCanvas` 翻译层（`SkiaChartRenderer`），只给 Avalonia 宿主（[Hevo.Charting.Avalonia](../Hevo.Charting.Avalonia)）用。主题资源笔刷 / 资源字体 / 宿主图片由宿主实现 `ISkiaHostResources` 提供。

图表的 Feature 投影、图层录制、DrawCmd 缓冲都不变，只把"指令 → 像素"这一步交给 Skia。

- SkiaSharp 不单独钉版本：本程序集引用 `Avalonia.Skia`，SkiaSharp 由它带进来，版本跟 Avalonia 走，进程里只有一份。
- 本程序集只用 SkiaSharp 的 API，不碰 Avalonia 控件；核心库 `Hevo.Charting.Core` 仍然零第三方依赖。
- WPF 宿主不走 Skia：WPF 下只有 `DrawingVisual` 矢量和位图模式（`RenderMode.Bitmap`）两条路。

## 画面口径

和 `WpfDrawingRenderer`（WPF 默认后端）一致的部分：

- 同样的像素对齐规则、同样的相邻 `DrawLine` 合批、`DrawRectangles` 逐个画，矩形填充关闭抗锯齿（边缘已对齐到设备像素，结果跟 WPF 的抗锯齿 + 设备像素对齐一致）；
- Pen 的语义：`HevoPen.LineCap` 按 WPF `PenLineCap` 解释（0 Flat / 1 Square / 2 Round；3 Triangle Skia 没有，退化为平头），`MiterLimit` = 10，虚线长度以线宽为单位、段端按 WPF 默认的 `DashCap = Square` 外扩半个线宽；
- 渐变：`HevoLinearGradientBrush` 与资源里的线性渐变按 `MappingMode` 处理，默认 `RelativeToBoundingBox` 时起止点按图元包围盒换算；资源笔刷的 `Opacity` 计入颜色；
- 路径标记：支持 WPF 的 `F0` / `F1` 填充规则前缀，没有前缀时默认 EvenOdd，与 `Geometry.Parse` 相同；
- 默认字体 Arial；主字体缺字形（如中文）时按字符做字体回退，不会出现豆腐块。

已知差异：

- 斜线、曲线、圆的抗锯齿由 Skia 计算，边缘像素的覆盖率与 WPF 不完全相同；
- 文字由 Skia 排版和光栅（字形 hinting、亚像素定位、回退字体的选择都可能与 WPF `FormattedText` 不同），宽度可能有零点几像素的差别；
- Triangle 线端画成平头；虚线的线首 / 线尾端按平头处理（中间各段的段端按 Square）；
- 径向等其它渐变只取首个色标当纯色；渐变画刷的 `Transform` / `RelativeTransform` 不支持；
- `DrawVideo` 不支持（该命令目前没有生产者）。

与位图后端（`RenderMode.Bitmap`，像素中心采样、无抗锯齿的参照实现）相比，水平 / 垂直线、矩形和线端、虚线的覆盖基本相同，差异主要在文字和斜线的抗锯齿上。

## 已知限制

- .NET 5：本程序集不在 [README §2](../Hevo.Charting/README.md) 必编清单里（Avalonia 12 最低 net8）。
