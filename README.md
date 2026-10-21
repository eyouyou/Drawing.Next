# Drawing.Next

> 面向金融场景的高性能 .NET 图表引擎。支持 WPF 与 Avalonia,千万级数据点、0-GC 渲染管线、声明式装配,内置低代码蓝图与 LLM 工具链。

## 简介

Drawing.Next 是一个通用的 .NET 图表引擎,重点服务于金融类产品:K 线、分时、技术指标、多图联动看板等对数据规模、交互响应和帧率要求较高的场景。

引擎核心与 UI 框架解耦。图表的数据流、图层与交互逻辑只需编写一次,即可运行在 WPF 或 Avalonia(Windows / macOS)上。

核心能力：

- **跨 UI 框架**：核心库不依赖任何 UI 框架,图层输出与平台无关的绘制指令,由宿主负责呈现。WPF 宿主提供位图、矢量、控件三层渲染(可选 Skia 合成);Avalonia 宿主基于 Skia 渲染,静态内容与交互内容分层合成。
- **0-GC 渲染管线**：列式内存布局（SoA）、`ArrayPool` 复用、原位更新；Project 帧严禁分配闭包，昂贵计算用 `ctx.UseMemo` 按依赖缓存。
- **自研响应式流（`IWorkflow<T>`）**：弃用 `System.Reactive`，搭配 `DataBlackboard` + `ReaderWriterLockSlim` 实现多引脚的"时空一致性"。
- **强类型引脚 `DataPort<T>`**：贯穿数据源 → 计算 → 渲染的物理线缆，杜绝魔术字符串与隐式耦合。
- **ECS 化 Feature 模型**：`ChartFeature` + `Trait` + `ReactiveSchema` 声明式装配，渲染 / 交互 / 逻辑彻底解耦。
- **低代码蓝图装配**：`ChartBlueprint`(JSON) + `BlueprintRunner` 在运行时孵化 `DynamicChartSchema<T>`，反射绑定全局引脚与 Feature。
- **Python 指标支持**：嵌入 CPython（pythonnet），`@indicator` / `@handler` 装饰器即写即用，蓝图侧零 Python 概念。
- **MCP server**：Model Context Protocol 工具集（`list_components` / `describe_component` / `validate_blueprint`），让 LLM 安全地编辑与校验蓝图。
- **编译期校验**：配套 Roslyn 分析器与源生成器在编译期拦截 0-GC、Hooks、生命周期等架构红线。

## Quick Start

第一次接触本项目:先读 [15 分钟上手](./docs/15分钟上手.md)(L1 改蓝图 JSON 加指标 → L2 写一个 C# 指标类 → L3 自定义 Trait + Layer),遇到生词查 [术语表](./docs/术语表.md)。

跑起来看看 WPF 示例（K 线 + 指标 + Python handler + Mock 交易面板）：

```bash
dotnet build "Drawing.Next.slnx" -nologo
dotnet run --project Hevo.Drawing.LowCodeDemo -c Debug
```

> 要求：Windows + 支持 WPF 的 .NET 10 SDK（当前 TFM 为 `net10.0-windows10.0.19041.0`）。首次启动会同步嵌入式 Python 312 运行时与 demo 指标。

Avalonia 示例(手写看板 / 蓝图 JSON 看板 / 蓝图编辑器三页,Windows / macOS 都能跑):

```bash
dotnet run --project Hevo.Charting.Avalonia.Demo
```

启动后能看到：低代码 Node Editor、内嵌的 K 线 Dashboard、AvalonEdit Python 编辑器、Mock 撮合面板。改完蓝图 JSON 热加载即可生效。

## 项目结构

| 项目 | 角色 | 说明 |
|------|------|------|
| [Charting.Core](./Hevo.Charting.Core) | 引擎核心(平台无关) | 抽象层、Feature 库、Layer、帧管线(`ChartEngine`)、黑板、指针路由、低代码蓝图运行时。不引任何 UI 框架,WPF / Avalonia 等宿主共用。 |
| [Charting](./Hevo.Charting) | WPF 宿主 | `ChartCell` 控件、WPF 渲染器 / 位图后端、控件类图层(Tooltip)、指针适配器、联动看板、低代码 Designer。业务工程引用它(连带引用 Core)。 |
| [Charting.Avalonia](./Hevo.Charting.Avalonia) | Avalonia 宿主 | `AvaloniaChartCell`、联动看板、头部、触屏、蓝图 / dashboard 启动器、蓝图编辑器、调试菜单;渲染只走 Skia。示例 `.Demo`、无窗口测试 `.Tests`。详见 [README](./Hevo.Charting.Avalonia/README.md)。 |
| [Charting.Skia](./Hevo.Charting.Skia) / [Skia.Wpf](./Hevo.Charting.Skia.Wpf) | 渲染后端 | DrawCmd → SKCanvas 翻译层(Avalonia 宿主用它上屏);WPF 下的可选 Skia 合成面。 |
| [Charting.Samples](./Hevo.Charting.Samples) | 示例共用 | 模拟数据源、手写 K 线 schema、示例看板蓝图 JSON,WPF / Avalonia 示例共用。 |
| [Charting.CodeAnalysis](./Hevo.Charting.CodeAnalysis) | 编译期 | Roslyn 分析器与源生成器：`UseMemo` 位置校验、跨帧缓存检测、Trait Consumer / Required 校验、Port Generator。 |
| [Drawing.LowCodeDemo](./Hevo.Drawing.LowCodeDemo) | 示例宿主 | 低代码蓝图运行时 + Node Editor + Python 编辑器 + Mock 撮合的 WPF 演示工程。 |
| [Charting.PythonNet](./Hevo.Charting.PythonNet) | 集成层 | Python 集成：`IPythonRuntime` + `PythonHandlerRegistry` + 蓝图层 `PyFeature` 节点。详见 [PYTHON_HANDLER_GUIDE](./Hevo.Charting.PythonNet/PYTHON_HANDLER_GUIDE.md)。 |
| [Charting.Mcp](./Hevo.Charting.Mcp) | LLM 工具链 | MCP 服务进程，向 LLM 暴露 ComponentRegistry 与 DryRun 校验器，闭合"see → describe → validate"循环。详见 [README](./Hevo.Charting.Mcp/README.md)。 |
| [Charting.Tests](./Hevo.Charting.Tests) | 测试 | xUnit 单元 / 集成测试(1200 多个用例);Avalonia 宿主的测试在 `Hevo.Charting.Avalonia.Tests`。 |
| [Charting.Tests/DocSamples](./Hevo.Charting.Tests/DocSamples) | 文档示例 | 开发规范 / 15 分钟上手引用的示例代码,按业务侧约束(`LangVersion 9` + 生成器)编译,由 `DocSampleTests` 回归。 |
| [Charting.Benchmarks](./Hevo.Charting.Benchmarks) | 基准 | BenchmarkDotNet 量化基准，覆盖蓝图编译化、反射 vs 编译 setter、端到端管线等。详见 [README](./Hevo.Charting.Benchmarks/README.md)。 |
| [Trade.Abstractions](./Hevo.Trade.Abstractions) | 交易抽象 | `ITradeService` 接口 + DTO + `NullTradeService`；不依赖 Charting / WPF / Skia，可独立引用。 |
| [Trade.Mock](./Hevo.Trade.Mock) | 交易实现 | `ITradeService` 的内存 Mock 实装，给 demo / 测试 / 策略回放用。 |

迁移到拆分后结构(核心 / 宿主)的业务改动见 [变更说明:核心拆分](./docs/变更说明-核心拆分-2026-10.md)。

## 文档导航

本文件是仓库唯一入口。**新成员先读 [15 分钟上手](./docs/15分钟上手.md) 和 [术语表](./docs/术语表.md);写代码前读 [开发准则](./Hevo.Charting/README.md)(.NET 5 兼容红线),再按需翻 00 → 01 → 04 系列。AI 助手看 [AGENTS.md](./AGENTS.md)。**

### 项目级

| # | 文档 | 受众 | 内容 |
|---|------|------|------|
| — | [15 分钟上手](./docs/15分钟上手.md) | 新成员 | 三级上手:L1 蓝图 JSON 加指标、L2 C# 指标类、L3 自定义 Trait + Layer。 |
| — | [术语表](./docs/术语表.md) | 全员 | 每个术语一句话,对照前端 / 通用概念。 |
| — | [开发准则](./Hevo.Charting/README.md) | 开发者 | .NET 5 兼容红线、常用命令、铁律摘要、注释规范。 |
| — | [AGENTS.md](./AGENTS.md) | AI 助手 | 唤醒词、阅读顺序、硬约束速记、文档约定。 |
| 00 | [架构协议与开发规范](./Hevo.Charting/00.架构协议与开发规范.md) | AI / 架构师 / 开发者 | 架构红线、System Prompt、ECS 协议、并发与上下文边界。**最高纲领文件。** |
| 01 | [业务 Feature 设计](./Hevo.Charting/01.业务Feature设计.md) | 业务开发者 | Feature 铁律、`OnCompose` / `OnProject` 切分、`DataBlackboard` / `UsePort` 物理读取限制。 |
| 04 | [交互层架构](./Hevo.Charting/04.交互层架构.md) | 交互 Feature 开发者 | 输入事件经指针路由器、`ChartInteractionFeature` 转化为视口变化与命中广播的完整链路。 |
| 06 | [WPF 渲染规约](./Hevo.Charting/06.WPF%20渲染规约.md) | 渲染层维护者 | 像素对齐契约、snap 实现、WPF 后端性能路线图。 |
| — | [Renderer.md](./Hevo.Charting/Renderer.md) | 渲染层维护者 | 接入新渲染后端（Skia / WPF / 自定义）的指南。 |
| — | [LowCode/Designer/低代码.md](./Hevo.Charting/LowCode/Designer/低代码.md) | 低代码 / 业务开发者 | 蓝图编译器、`ChartBlueprint` / `BlueprintRunner` / `ComponentRegistry` 六件套。 |
| — | [TODO.md](./Hevo.Charting/TODO.md) | 全员 | 触发条件 + 改动方案的技术债。 |

### 模块级

| 文档 | 受众 | 内容 |
|------|------|------|
| [Charting.Mcp/README.md](./Hevo.Charting.Mcp/README.md) | LLM 集成 | MCP 工具说明、安装到 Claude / Cursor 的步骤、JSON schema。 |
| [Charting.PythonNet/PYTHON_HANDLER_GUIDE.md](./Hevo.Charting.PythonNet/PYTHON_HANDLER_GUIDE.md) | 策略 / 指标开发者 | Python handler 写法、`@indicator` 装饰器、PortBindings 反射、热重载、诊断。 |
| [Charting.Benchmarks/README.md](./Hevo.Charting.Benchmarks/README.md) | 性能维护者 | 跑法、实测数据、§1–§6 优化方案对照。 |

## 架构核心一图流

```text
[数据源] DataSource(BufferedDataSource / ReactiveDataSource)
    │  source.Pipe()
    ▼
====================【数据管线】====================
    ├─► 摄入 LinkStream : TItem → 列 (ReadOnlyMemory<T>) 写进黑板
    ├─► 管线内计算 .Compute (可选) : 与摄入同一个写事务
    │
  [DataBlackboard:端口 → 列 / 标量]
====================================================
    │  .BindTo(chart)
    ▼
[Feature]
    ├─► 计算 Feature : ComputeFeature / PlotFeature,后台算指标写回黑板
    ├─► 渲染 Feature : OnProject 里 UsePort 读黑板 → 发布 Trait
    ▼
[ChartLayer] 录制绘制指令 → 宿主上屏:WPF DrawingVisual (Software) / 位图回放 (Bitmap) / Widget (Interaction);Avalonia SKPicture 回放
```

## 不可逾越的红线（节选）

完整列表与判例见 [00 号文档](./Hevo.Charting/00.架构协议与开发规范.md)，下面是最常踩的几条：

- **0-GC**：`Project` 帧不 `new` 集合 / 数组 / 闭包（工作集合做成跨帧复用字段；每帧发布的不可变 Trait 记录除外），昂贵计算只走 `ctx.UseMemo`(factory 写成 `static` lambda)。
- **Hook 规则**：Hook 只在 `OnProject` 帧路径里调；`UseMemo` 直接写在 `OnProject` 里，不写进 helper / 循环 / lambda / 本地函数（HEVO008，Debug 构建下运行时也会拦）。`UsePort` 可以放进分支：没读的帧不更新版本，下次读到时 `IsChanged` 跟上次读到时比。
- **物理坐标隔离**：禁止手写 `(x - min) / span * width`，一律走 `IScale.Normalize`。
- **Feature 不持有 DataSource**：业务 Feature 只认 `DataPort<T>` 引脚，所有数据装配统一走 `Schema.DefineDataFlow`。
- **禁用 `System.Reactive`**：全盘走自研 `IWorkflow<T>`。

## 构建与测试

按项目单独编译（推荐）：

```bash
dotnet build "Hevo.Charting.Core/Hevo.Charting.Core.csproj" -nologo
dotnet build "Hevo.Charting/Hevo.Charting.csproj" -nologo
dotnet build "Hevo.Charting.CodeAnalysis/Hevo.Charting.CodeAnalysis.csproj" -nologo
dotnet build "Hevo.Drawing.LowCodeDemo/Hevo.Drawing.LowCodeDemo.csproj" -nologo
```

整解决方案：

```bash
dotnet build "Drawing.Next.slnx" -nologo
```

测试 / 基准：

```bash
dotnet test  "Hevo.Charting.Tests/Hevo.Charting.Tests.csproj" -c Release
dotnet run --project Hevo.Charting.Benchmarks -c Release -- --filter "*"
```

发布(ReadyToRun 预编译,省冷启动 JIT;LowCodeDemo / Mcp / Benchmarks 都带 `ReadyToRun` 发布配置,输出到 `bin/publish/r2r/`):

```bash
dotnet publish "Hevo.Drawing.LowCodeDemo/Hevo.Drawing.LowCodeDemo.csproj" -c Release -p:PublishProfile=ReadyToRun
```

> 实测首张图冷启动(装配 + 首帧)JIT 约 242ms → R2R 约 194ms,热启动不变;数据见 [Benchmarks README](./Hevo.Charting.Benchmarks/README.md)。

> 目标平台：Any CPU / x64。核心库、Skia 渲染层与 Avalonia 宿主面向 `net10.0`,支持 Windows 与 macOS;WPF 宿主及依赖它的 MCP、测试、基准工程面向 `net10.0-windows10.0.19041.0`。
