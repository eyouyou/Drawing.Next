# Hevo.Charting.Samples

示例共用的模拟数据源,不引任何 UI 框架,WPF 示例(`Hevo.Drawing.LowCodeDemo`)和 Avalonia 示例(`Hevo.Charting.Avalonia.Demo`)引用同一份。

| 类型 | 内容 |
|---|---|
| `MockKLineDataSource` / `MockKLineBar` | 随机游走 K 线:首次拉取 30 根,之后每 150ms 追加一根,1000 根封顶;进程内所有实例共享同一条时间线 |
| `SinWaveDataSource` / `SinWavePoint` | 200 点正弦波,一次性给完 |

蓝图 JSON 里按类型名引用(`"TypeName": "MockKLineDataSource"`),宿主启动时 `BuiltinRegistration.RegisterAssemblyOf<SinWaveDataSource>()` 登记本程序集。

**推送定时器由宿主提供**:启动时设 `MockKLineDataSource.StartTimer`,回调要在 UI 线程跑(WPF 示例见 `App.xaml.cs`,Avalonia 示例见 `Program.cs`)。不设就不推送,只有首批 30 根,单测里就是这样。

LowCodeDemo 引用本工程,所以按业务工程的红线编译:.NET 5 SDK 5.0.408 能重编(在 `scripts/check-net5.ps1` 里)、LangVersion 9、不开 ImplicitUsings。
