# AGENTS.md — AI 协作准则

给在本仓库写代码的 AI 助手(Claude Code / Codex / Cursor 等)。人类新成员请从 [README.md](./README.md) 开始。

## 唤醒词

新任务起手用这段锁定风格:

> 请按 Hevo.Charting 项目规范执行:严守 .NET 5 兼容(SDK 5.0.408 红线,见 `Hevo.Charting/README.md` §2)、`00.架构协议与开发规范` 全部铁律、`Environment / Axes / Series / Interactions` 四段 builder 装配、注释只阐述 Why / Intent。

## 写代码前读什么

1. [Hevo.Charting/README.md](./Hevo.Charting/README.md):.NET 5 兼容红线、常用命令、注释规范。**红线不可协商。**
2. [术语表](./docs/术语表.md):黑板 / 端口 / Feature / Trait / Layer 各是什么。
3. [00 号文档](./Hevo.Charting/00.Hevo.Charting%20架构协议与开发规范.md):架构铁律与 SOP。新写指标 / 图层前看 §5,代码范例在 `Hevo.Charting.Tests/DocSamples/`。
4. 按任务翻:蓝图 → `LowCode/Designer/低代码.md`;交互 → `04`;渲染 → `06` / `Renderer.md`;Python → `Hevo.Charting.PythonNet/PYTHON_HANDLER_GUIDE.md`。

## 硬约束速记

- `Hevo.Charting` / `Hevo.Charting.CodeAnalysis` / 业务工程必须能用 .NET 5 SDK 5.0.408 重编:不用 `required`、集合表达式 `[]`、`ArgumentNullException.ThrowIfNull` 等 .NET 6+ / C# 11+ 特性;业务工程(`Hevo.Drawing.LowCodeDemo`、`DocSamples`)锁 `LangVersion 9`。测试 / 基准 / MCP 工程不受限。
- `OnProject` 里 `UsePort` / `UseMemo` 顶层、无条件、固定顺序;不写黑板;不 `new` 集合 / 数组 / 闭包(工作集合做成跨帧复用字段;每帧发布的不可变 Trait 记录除外)。
- 坐标只走 `IScale.Normalize` / `CoordinateExtensions.ProjectToScreen`,不手写 `(x - min) / span * width`。
- Layer 用 `partial class`,`IConsumes<T>` 交给生成器,不手写。
- 往端口发布的列每次一块新缓冲(`ColumnPublisher<T>` / `ResultBufferPool`),不原地改已发布数组。
- 不引入 `System.Reactive`。

## 验证

```bash
dotnet build "Hevo.Charting/Hevo.Charting.csproj" -nologo
dotnet test "Hevo.Charting.Tests/Hevo.Charting.Tests.csproj" -nologo
```

改了文档里引用的示例(`Hevo.Charting.Tests/DocSamples/`)要跑 `--filter FullyQualifiedName~DocSampleTests`。

## 写文档的约定

- 对外文档(README、00–06、上手、术语表)不写内部施工编号(如 "Phase 11"、"§H"、"L6 / §B.2.6"、"§D2.X");需要追溯就写 commit 或 PR。
- 代码范例放进 `Hevo.Charting.Tests/DocSamples/` 让它随测试编译,文档只引用文件,不在 md 里贴会过期的手抄副本。
- 提到项目、API、命令前先确认它在当前分支存在。
