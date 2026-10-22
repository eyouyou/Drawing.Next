global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;

// 暴露 internal API 给周边工程项目(benchmark 直调避免反射污染数据,test 内省缓存等)。
// 其他外部消费方仍受 internal 约束。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hevo.Charting.Benchmarks")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hevo.Charting.Tests")]
// Skia 后端直接读 DrawingBuffer 指令与像素对齐工具(与 WPF / 位图后端同口径),不经公开 API 拷贝
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hevo.Charting.Skia")]
