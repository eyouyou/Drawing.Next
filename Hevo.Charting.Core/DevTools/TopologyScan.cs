using Hevo.Charting.Core;
using Hevo.Charting.LowCode;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Hevo.Charting.DevTools
{
    /// <summary>拓扑图上的一个节点(数据源 / 端口 / Feature / 图层)。Tier:0 源头 / 传感器,1 端口,2 Feature,3 图层。</summary>
    public readonly struct TopologyNodeInfo
    {
        public TopologyNodeInfo(string id, string label, int tier) { Id = id; Label = label; Tier = tier; }
        public string Id { get; }
        public string Label { get; }
        public int Tier { get; }
    }

    /// <summary>
    /// 拓扑监控的静态骨架扫描:schema 里有哪些 Feature、各自声明了哪些端口。只出节点不出连线 ——
    /// 连线全部来自运行时追踪(<see cref="TopologyTracer.LinkHits"/>),图上不画猜出来的线。
    /// 各宿主的监控面板(WPF TopologyInspectorControl / Avalonia 拓扑窗)共用。
    /// </summary>
    public static class TopologyScan
    {
        public static List<TopologyNodeInfo> ScanNodes(IChartCell cell)
        {
            var nodes = new List<TopologyNodeInfo>();
            var schema = cell.Template as ReactiveSchema;
            if (schema == null) return nodes;

            // 1. 数据管线源头
            nodes.Add(new TopologyNodeInfo("PIPE", "DataPipe (源)", 0));

            // 2. 扫描 Schema 里的原生引脚
            foreach (var p in ExtractPorts(schema))
            {
                if (nodes.All(n => n.Id != p.Id)) nodes.Add(new TopologyNodeInfo(p.Id, p.DisplayName, 1));
            }

            // 3. 扫描所有的 Features 以及它们内部偷偷 new 的引脚
            foreach (var f in schema.Features)
            {
                var fId = FeatureId(f);
                var baseName = CleanName(f.GetType().Name);
                // 同类型多实例(LineSeriesFeature × N)在图谱里原本完全无法区分,
                // 拼上 InstanceId 前 4 位让多实例可视化分得清。
                var fName = f is ChartFeature cf && !string.IsNullOrEmpty(cf.InstanceId)
                    ? $"{baseName}#{cf.InstanceId.Substring(0, Math.Min(4, cf.InstanceId.Length))}"
                    : baseName;
                bool isSensor = baseName.Contains("Interaction");
                nodes.Add(new TopologyNodeInfo(fId, fName, isSensor ? 0 : 2));

                foreach (var p in ExtractPorts(f))
                {
                    if (nodes.All(n => n.Id != p.Id)) nodes.Add(new TopologyNodeInfo(p.Id, p.DisplayName, 1));
                }
            }
            return nodes;
        }

        /// <summary>追踪器里 Feature 的节点 id(跟 <see cref="TopologyTracer"/> 记录时同一套规则)。</summary>
        public static string FeatureId(object feature) => $"F_{RuntimeHelpers.GetHashCode(feature)}";

        /// <summary>对象(schema / Feature)字段与属性里的端口;ViewportPorts 展开成三根。</summary>
        public static List<IDataPort> ExtractPorts(object target)
        {
            var res = new List<IDataPort>();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var members = target.GetType().GetFields(flags).Cast<MemberInfo>()
                          .Concat(target.GetType().GetProperties(flags));

            foreach (var m in members)
            {
                object? v = m is FieldInfo fi ? fi.GetValue(target) : ((PropertyInfo)m).GetValue(target);
                if (v is IDataPort dp) res.Add(dp);
                else if (v != null && v.GetType().Name == "ViewportPorts") res.AddRange(ExtractPorts(v));
            }
            return res;
        }

        private static string CleanName(string name) => name.Replace("Feature", "").Split('`')[0];
    }
}
