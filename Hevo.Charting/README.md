# Charting 开发准则

引擎核心工程的开发准则:.NET 5 兼容红线、常用命令、架构铁律摘要、注释规范。
仓库总入口是根目录 [README.md](../README.md);AI 协作约定在 [AGENTS.md](../AGENTS.md)。深入架构读 `00`–`06` 系列。

---

## 1. 仓库范围

| 工程 | 说明 | 受 .NET 5 兼容约束 |
|------|------|------|
| `Hevo.Charting` | 引擎核心:workflow、schema、feature、黑板、渲染管线、低代码蓝图 | 是 |
| `Hevo.Charting.CodeAnalysis` | Roslyn 生成器 / 分析器(PortGenerator、IConsumes 生成、Hook 校验) | 是 |
| `Hevo.Drawing.LowCodeDemo` | 示例宿主(业务侧写法的参照),`LangVersion 9` | 是 |
| `Hevo.Charting.Tests/DocSamples` | 文档引用的示例代码,按业务侧约束编译,`LangVersion 9` | 是 |
| `Hevo.Charting.PythonNet` / `Hevo.Trade.*` | Python 集成 / 交易抽象与 Mock | 见各自 csproj |
| `Hevo.Charting.Tests` / `Hevo.Charting.Benchmarks` / `Hevo.Charting.Mcp` | 测试 / 基准 / MCP 服务 | 否(net10 only) |

---

## 2. ⚠️ 维持 .NET 5 兼容(硬性约束)

虽然项目当前编译目标是 **.NET 10**,但全部新增/修改代码 **必须保持可在 .NET 5 (SDK 5.0.408 + Roslyn 3.x) 下重新编译通过**。这是项目级硬约束,不是可选偏好。任何引入 .NET 6+/Roslyn 4+ 独占特性的提交都视为破坏约束,需要回退或提供等价兼容方案。

### 核心红线

范围:`Hevo.Charting`、`Hevo.Charting.CodeAnalysis`、业务工程(`Hevo.Drawing.LowCodeDemo`、`DocSamples`)以及业务工程引用到的 `Hevo.Charting.PythonNet`、`Hevo.Trade.*`。`scripts/check-net5.ps1` 按下面"切回 .NET 5 的清单"自动检查,CI 的 net5-compat job 每次都跑。这些工程不开 `ImplicitUsings`(.NET 5 SDK 不认),统一用 `Globals.cs` / `GlobalUsings.cs` 写 global using。

1. **业务侧 (`Hevo.Drawing.LowCodeDemo`、`DocSamples` 等下游工程) 严格 `LangVersion=9.0`**。csproj 已显式锁定。禁止使用 record struct、global using、file-scoped namespace、collection expression `[]`、`required` 修饰符、原始字符串字面量等 C# 10+ 特性。
2. **Framework 侧 (`Hevo.Charting`) `LangVersion=preview`**,可在 `.NET 5 SDK 5.0.408` 下解锁 record struct + global using;但仍只允许使用 .NET 5 SDK preview 编译器实际能编译的 C# 10 特性子集。**禁止使用 `required` (C# 11)、collection expression (C# 12) 等 .NET 5 SDK 不识别的特性**。
3. **BCL API 红线** — 不得使用以下 .NET 6+ API:
   - `ArgumentNullException.ThrowIfNull` → 用 `if (x is null) throw new ArgumentNullException(nameof(x))`
   - `Task.WaitAsync(CancellationToken)` → 手写 `CancellationTokenRegistration` + TCS 等价模式
   - `DateOnly` / `TimeOnly` / `PriorityQueue` / `Random.Shared` / `TimeProvider` / `SearchValues` / `FrozenDictionary` 等
4. **Roslyn 红线 — Generator 不得使用 `IIncrementalGenerator`**。所有 generator 必须实现 `ISourceGenerator + ISyntaxContextReceiver`(失去增量缓存的代价)。`Hevo.Charting.CodeAnalysis` 的 NuGet 引用 **钉死 Microsoft.CodeAnalysis.CSharp 3.11.0**。
5. **WPF 编译 quirk** — 业务工程(如 `Hevo.Drawing.LowCodeDemo.csproj`)用 `<Analyzer Include="...dll" />` 直接注册 generator,而 `<ProjectReference>` 不带 `OutputItemType=Analyzer`。**这是为了绕过 .NET 5 SDK 的 `_wpftmp` 编译不继承 Analyzer 类型 ProjectReference 的 bug**。net10 已修但保留此写法以维持双向可切。
6. **包版本** — 例如 `System.Text.Encoding.CodePages` 钉 5.0.0;若升级请同步评估 net5 是否仍能解析。
7. **类型选型** — `IVisualTrait`(进 blackboard)用 `record class`;value payload(`HevoPoint` / `FieldMeta` / `PointerHitState` 等)用 `record struct` 或 `record class`(具体见 `00.架构协议与开发规范`)。**`record struct` 的 `with` 表达式是 C# 10 特性**,若调用方在 `LangVersion=9` 下,必须提供 `WithXxx(...)` helper 替代。

### 切回 .NET 5 的清单(测试用)

如果需要验证 net5 兼容是否被破坏,把以下几处改回即可重现:
- `Hevo.Charting/Hevo.Charting.csproj`:`<TargetFramework>net5.0-windows10.0.19041.0</TargetFramework>`
- 要一起验证的业务工程(如 `Hevo.Drawing.LowCodeDemo.csproj`):同样改成 `net5.0-windows10.0.19041.0`
- 项目根新建 `global.json`:`{"sdk":{"version":"5.0.408","rollForward":"disable"}}`

预期 `Hevo.Charting.CodeAnalysis` / `Hevo.Charting` 及改过的业务工程全部 0 error 通过。

### 写代码时必须做的

- 引入新的 BCL API 前,在 [learn.microsoft.com](https://learn.microsoft.com/dotnet/api) 查 "Applies to" 是否包含 .NET 5;不包含就找等价手写实现或加 polyfill。
- 添加新的 generator/analyzer 前,确认只用 Microsoft.CodeAnalysis 3.11 已有的 API。
- 在业务工程内写新文件时,**手写完整 `using` 列表**(System / Collections.Generic / Linq / Threading / Threading.Tasks 等),不要依赖 ImplicitUsings — 业务工程没开启它。

---

## 3. 常用命令

按工程单独编译:

```bash
dotnet build "Hevo.Charting/Hevo.Charting.csproj" -nologo
dotnet build "Hevo.Charting.CodeAnalysis/Hevo.Charting.CodeAnalysis.csproj" -nologo
dotnet build "Hevo.Drawing.LowCodeDemo/Hevo.Drawing.LowCodeDemo.csproj" -nologo
```

整解决方案:

```bash
dotnet build "Hevo.Drawing.slnx" -nologo
```

测试(xUnit,400 多个用例;文档示例的回归在 `DocSampleTests`):

```bash
dotnet test "Hevo.Charting.Tests/Hevo.Charting.Tests.csproj" -nologo
```

注意:
- 仅修改引擎或某个业务工程时,优先编译受影响的工程,不必跑整 sln。
- 真 Python 端到端测试需要仓库根的 `Python312/` 运行时(见 `scripts/`),没有时这些用例自动跳过。

---

## 4. 架构铁律(摘要)

完整规约见 `00.架构协议与开发规范`。下列是日常写代码必须随手记得的红线:

- 不引入 `System.Reactive`,只用自研 `IWorkflow<T>`。
- 数据进黑板后只通过 `DataPort<T>` 通信,业务实体泛型 `TItem` 仅停留在数据接入侧。
- Hook(`UsePort` / `UseMemo`)只在 `OnProject` 帧路径里调;`UseMemo` 直接写在 `OnProject` 里,不写进 helper / 循环 / lambda / 本地函数(HEVO008,Debug 构建下运行时也会拦);严禁在 `OnProject` 内写 `DataBlackboard`,严禁跨帧缓存 `UsePort` 返回的 memory。
- Feature 执行顺序由 `FeaturePhase` 控制,严禁靠 `List.Add` 顺序硬编码。
- 物理坐标必须经 `IScale.Normalize`,严禁手写 `(x - min) / span * width` 之类反推公式。
- 运行时增删 Feature 必须走 `Transact(...)`,不要散装 `Add/Remove`。
- 业务侧首选 `Environment / Axes / Series / Interactions` 四段 builder,优先 Roslyn 生成的 ports/mapping 替代手写样板。

---

## 5. 注释规范

> **核心哲学**:关键逻辑不落空,废话半句都不多。
> 注释只解释 **「为什么 (Why)」** 与 **「意图 (Intent)」**,不翻译 **「是什么 (What)」**。

### 准则一:意图导向,拒绝"废话文学"

不要把 C# 语法翻译成中文。注释要说明这段代码在 **业务链路** 或 **架构约束** 中的真实作用。

❌ 翻译语法:
```csharp
// 如果 range 无效,就 return
if (!range.IsValid) return;
```

✅ 阐述意图:
```csharp
// 💥 0-GC 短路拦截:数据不足或未初始化时直接退帧,防止无效计算击穿底层渲染
if (!range.IsValid) return;
```

### 准则二:死磕"魔法数字"与数学公式

任何脱离上下文的常量(如 `0.5`、`12`)或复杂数学,**必须**配推导注释。

❌ 不明所以的常量:
```csharp
double padding = maxDiff * 1.1;
```

✅ 标注物理意义:
```csharp
// 叠加 10% 安全边距 (PaddingRatio),防止极值点紧贴画板上下边缘
double padding = maxDiff * (1 + PaddingRatio);
```

### 准则三:标明"架构红线"与"物理约束"

违背常规直觉的写法(为遵守 Hook 规则 / 绕系统 bug / 极致优化)必须用 `// 💥` 醒目标记。

```csharp
// 💥 同一帧同一端口只 UsePort 一次:第二次拿到的 IsChanged 恒为 false,要多处用就把这次的结果传下去
var (range, rangeChanged) = ctx.UsePort(RangePort);
```

### 准则四:解释"响应式依赖"的触发边界

使用 `UseMemo` / 响应式流时必须简要说明 **什么情况下会重算**。

```csharp
// 缓存刻度。依赖 plotArea 和 range,当且仅当窗口拉伸或数据量程变化时触发重算
var (ticks, ticksChanged) = ctx.UseMemo((range, plotArea), static deps => { ... });
```

### 自检清单

- [ ] 是否有"翻译"语法?(有则删)
- [ ] 魔法数字是否标注了物理含义?
- [ ] 复杂的 `if` 是否有业务理由?
- [ ] Hook 是否只在 `OnProject` 帧路径里调?`UseMemo` 是否直接写在 `OnProject` 里(不在 helper / 循环 / lambda 里)?只想知道值变没变的地方,是不是直接跟字段比、没套 `UseMemo`?
- [ ] 生命周期敏感代码(`OnSuspend / OnResume / Dispose`)是否说明了为何需要手动处理(见 00 号文档 §14 可暂停生命周期)?

---

## 6. 文档地图

完整导航见根目录 [README.md](../README.md#文档导航)。本目录下:

- `00.架构协议与开发规范.md` — 架构红线、铁律、SOP(主参考)
- `01.业务Feature设计.md` — 业务侧 Feature 拆解模式
- `04.交互层架构.md` — 鼠标 / 键盘 / 滚轮 → 视口数学
- `06.WPF 渲染规约.md` — 像素对齐契约 / snap 实现 / WPF 后端性能路线图
- `Renderer.md` — 接入新渲染后端的指南
- `TODO.md` — 触发条件 + 改动方案的技术债
- `LowCode/Designer/低代码.md` — 蓝图编译器与 ChartBlueprint
