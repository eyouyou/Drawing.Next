namespace Hevo.Charting.DevTools
{
#if DEBUG
    /// <summary>把追踪器记下的拓扑转成 WPF 监控面板的节点 / 连线(带布局用的 Rect)。</summary>
    public static class TopologyTracerWpfExtensions
    {
        public static (List<TopoNode> Nodes, List<TopoLink> Links) DumpTopology(this TopologyTracer tracer)
        {
            var nodes = new List<TopoNode>();
            var links = new List<TopoLink>();
            var activeNodeIds = new HashSet<string>();

            foreach (var kvp in tracer.LinkHits)
            {
                var (src, tgt) = kvp.Key;
                activeNodeIds.Add(src);
                activeNodeIds.Add(tgt);
                links.Add(new TopoLink { Src = src, Tgt = tgt, HitCount = kvp.Value });
            }

            foreach (var id in activeNodeIds)
            {
                if (tracer.NodeMetadata.TryGetValue(id, out var meta))
                    nodes.Add(new TopoNode { Id = id, Label = meta.Name, Tier = meta.Tier });
            }

            return (nodes, links);
        }
    }
#endif
}
