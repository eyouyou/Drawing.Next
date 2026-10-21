using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Hevo.Charting.DevTools
{
    // ==========================================
    // 1. 静态骨架扫描器 (负责在启动时画出基础架构)
    // ==========================================
    /// <summary>WPF 监控面板用的静态骨架扫描;扫描本身在核心 <see cref="TopologyScan"/>(Avalonia 拓扑窗共用)。</summary>
    public static class TopologyScanner
    {
        // 💥 签名改了：只返回 Nodes，不再返回任何 Links！
        public static List<TopoNode> Scan(IChartCell cell)
            => TopologyScan.ScanNodes(cell).Select(n => new TopoNode { Id = n.Id, Label = n.Label, Tier = n.Tier }).ToList();

        // 暴露给 TopologyInspectorControl 用的 wrapper —— 同 ExtractPorts 但 public。
        public static List<IDataPort> ExtractPortsPublic(object target) => TopologyScan.ExtractPorts(target);
    }

}
