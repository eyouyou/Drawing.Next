using Hevo.Charting.DevTools;
using Hevo.Charting.Features;
using Hevo.Charting.LowCode;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// <see cref="GraphSerializer"/> 的反向桥:把已有的 <see cref="ChartBlueprint"/> JSON 还原成
    /// 可在 GraphViewer 编辑器里继续拖动/改连线的 <see cref="GraphState"/>。
    /// <para>
    /// 用途:业务侧把生产蓝图 JSON 灌进编辑器二次调整(等价于把 ChartBlueprint 当成持久层,
    /// 编辑器当成 IDE)。低代码.md §7.12 待补清单的"反序列化已有 ChartBlueprint JSON → 还原成 GraphState"。
    /// </para>
    /// <para>
    /// <b>规则</b>:
    /// <list type="number">
    ///   <item><b>节点</b>:每个 DataSource / Feature / Trait 走 <see cref="NodeFactory.CreateNode"/> 派生(端口元数据按 .NET 反射重建);
    ///         位置由内部 <see cref="AutoLayout"/> 按 <see cref="FeatureCategory"/> 分竖向泳道、泳道内按数据流分列自动派。</item>
    ///   <item><b>Viewport</b>:扫描所有 <c>PortBindings</c> + <c>DataSource.ScalarMappings</c>,
    ///         发现指向 <c>VP_LogicalLength</c> / <c>VP_UserRange</c> / <c>VP_ActiveRange</c> 这几个 well-known id 时,
    ///         自动孵化一个 <see cref="NodeKind.Viewport"/> 节点并把对应连线引过去 —— 跟 GraphSerializer 的 Viewport 路径对称。</item>
    ///   <item><b>Edges</b>:每个全局 portId 反查"是哪个节点的哪个 OutputPort 写出来的",对每个引用它的 InputPort 拉一根边。
    ///         数组扇入端口(CSV)拆开后逐个还原。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>遗漏与降级</b>:
    /// <list type="bullet">
    ///   <item>蓝图里出现但 ComponentRegistry 未登记的 TypeName → 忽略整个节点(打印警告,不抛)。
    ///         避免"业务自定义 feature 没注册就把整个画布带崩"。</item>
    ///   <item>PortBindings 引用的 globalId 在所有 OutputPort 反查表里找不到来源 → 该 Edge 直接丢,
    ///         保留节点。这种情况通常发生在蓝图被人手撸过、或者业务自定义 PortGenerator 派的 portId。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class GraphDeserializer
    {
        /// <summary>
        /// 把 <see cref="ChartBlueprint"/> 还原成可编辑的 <see cref="GraphState"/>。
        /// 失败原因(类型未注册、端口反查不到等)走 <see cref="Console.WriteLine"/> 警告,不抛。
        /// </summary>
        public static GraphState FromBlueprint(ChartBlueprint blueprint)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));

            // 旧格式字段先迁移,否则画布上看不到它们表达的连线,保存后就真丢了。
            foreach (var d in BlueprintLegacyMigrator.Migrate(blueprint))
                Console.WriteLine($"[Graph 反序列化] {d}");

            var nodes = new List<Node>();
            // globalId → (nodeId, outputPortId) 反查表:边重建时把 PortBindings 里的 globalId 翻成源端坐标。
            var globalIdToOutput = new Dictionary<string, (string nodeId, string portId)>();

            // 1. DataSource 节点(节点化协议:多 DS 平等,跟 Feature 走同款 Properties + PortBindings)
            // 画不出来的模型(类型未登记等)收进 passthrough,保存时原样写回,不替用户删配置。
            var passDs = new List<DataSourceModel>();
            var passTraits = new List<StyleModel>();
            var passFeatures = new List<FeatureModel>();

            var dsModelByNodeId = new Dictionary<string, DataSourceModel>(StringComparer.Ordinal);
            foreach (var dsm in blueprint.DataSources)
            {
                if (string.IsNullOrEmpty(dsm.TypeName)) continue;
                var n = TryCreateDataSourceNode(dsm, blueprint, globalIdToOutput);
                if (n != null)
                {
                    nodes.Add(n);
                    dsModelByNodeId[n.Id] = dsm;
                }
                else passDs.Add(dsm);
            }

            // 2. Trait 节点(InitialTraits)
            foreach (var sm in blueprint.InitialTraits)
            {
                var n = TryCreateTraitNode(sm);
                if (n != null) nodes.Add(n);
                else passTraits.Add(sm);
            }

            // 3. Feature 节点 —— 同时构建 featureModelByNodeId 字典(避免旧 Zip 顺序 bug:
            //    TryCreateFeatureNode 返 null 时 featureNodes list 与 blueprint.Features list 错位)。
            var featureModelByNodeId = new Dictionary<string, FeatureModel>(StringComparer.Ordinal);
            foreach (var fm in blueprint.Features)
            {
                var n = TryCreateFeatureNode(fm, globalIdToOutput);
                if (n != null)
                {
                    nodes.Add(n);
                    featureModelByNodeId[n.Id] = fm;
                }
                else passFeatures.Add(fm);
            }

            // 4. Viewport 节点 —— 蓝图里只要任何一处引用了 VP_* well-known id,就孵化一个 Viewport 节点。
            //    它的 OutputPort globalId 写进反查表,这样从 Viewport 出去的连线也能正确拼出来。
            if (BlueprintReferencesViewport(blueprint))
            {
                var viewportNode = NodeFactory.CreateViewportNode(new HevoPoint(0, 0));
                nodes.Add(viewportNode);
                foreach (var op in viewportNode.OutputPorts)
                {
                    var wk = NodeFactory.ViewportWellKnownId(op.Id);
                    if (wk != null) globalIdToOutput[wk] = (viewportNode.Id, op.Id);
                }
            }

            // 5. Codec dispatch:边重建全部委托给各 codec
            //    - DataEdgeCodec:DS→Viewport 边 + Feature InputPort 边
            //    - CascadeEdgeCodec:bp.Cascades → Kind=Cascade 边
            //    - UpstreamRefCodec:dsm.UpstreamRefs → DS.Stream→Composite.Upstreams 边
            var edges = new List<Edge>();
            var ctx = new EdgeDecodeContext(
                blueprint, nodes, nodes.ToDictionary(n => n.Id),
                globalIdToOutput, featureModelByNodeId, dsModelByNodeId, edges);
            foreach (var codec in EdgeKindCodecs.All)
                codec.Decode(ctx);

            // 扇入端口上解析不到画布内源头的引用(dashboard 共享端口、未登记节点的输出)记到端口上,保存时写回。
            ApplyExternalFanInIds(nodes, featureModelByNodeId, globalIdToOutput);

            // 6. 自动布局:竖向泳道从左到右 数据源 → Environment → Axes → Series → Interaction,连线走布局给的折线。
            var routes = AutoLayout.Apply(nodes, edges);

            return new GraphState(
                Nodes: nodes,
                Edges: edges,
                SelectedNodeIds: new HashSet<string>(),
                Transform: CanvasTransform.Identity,
                RubberBand: null,
                BoxSelection: null,
                EdgeRoutes: routes)
            {
                Passthrough = new BlueprintPassthrough(
                    passDs, passTraits, passFeatures,
                    blueprint.Triggers.ToList(), blueprint.TriggerBindings.ToList(),
                    blueprint.ExtensionData == null ? null : new Dictionary<string, System.Text.Json.JsonElement>(blueprint.ExtensionData)),
            };
        }

        private static void ApplyExternalFanInIds(
            List<Node> nodes,
            Dictionary<string, FeatureModel> featureModelByNodeId,
            Dictionary<string, (string nodeId, string portId)> globalIdToOutput)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!featureModelByNodeId.TryGetValue(node.Id, out var fm)) continue;
                Port[]? ports = null;
                for (int j = 0; j < node.InputPorts.Count; j++)
                {
                    var p = node.InputPorts[j];
                    if (!p.IsArray || !fm.InputBindings.TryGetValue(p.Id, out var raw)) continue;
                    var external = PortBindingValue.ExtractList(raw)
                        .Where(id => !string.IsNullOrEmpty(id) && !globalIdToOutput.ContainsKey(id))
                        .ToList();
                    if (external.Count == 0) continue;
                    ports ??= node.InputPorts.ToArray();
                    ports[j] = p with { ExternalBindingIds = external };
                }
                if (ports != null) nodes[i] = node with { InputPorts = ports };
            }
        }

        // ==========================================
        //  节点构造
        // ==========================================

        private static Node? TryCreateDataSourceNode(DataSourceModel dsm, ChartBlueprint blueprint, Dictionary<string, (string, string)> globalIdToOutput)
        {
            Type? dsType;
            if (IsCompositeSentinel(dsm.TypeName))
            {
                // Composite sentinel:从 UpstreamIds[0] 或 Upstreams[0] 反推 TItem 闭合 Composite<TItem>。
                // 跟 BlueprintRunner.ResolveCompositeGenericTypeWithBlueprint 同款逻辑(编辑器加载也要一致)。
                dsType = ResolveCompositeTypeForGraph(dsm, blueprint);
                if (dsType == null)
                {
                    Console.WriteLine($"[Graph 反序列化警告] Composite '{dsm.Id}' 反推 TItem 失败,忽略整个节点。");
                    return null;
                }
            }
            else
            {
                try { dsType = ComponentRegistry.Resolve(dsm.TypeName); }
                catch { Console.WriteLine($"[Graph 反序列化警告] DataSource '{dsm.TypeName}' 未登记,忽略整个数据源节点。"); return null; }
            }

            var node = NodeFactory.CreateNode(dsType, new HevoPoint(0, 0));
            // 节点 Id 优先取蓝图里的 Id,缺失时走 NodeFactory 自动生成的 Id(单 DS 蓝图老资产兜底)。
            if (!string.IsNullOrEmpty(dsm.Id)) node = node with { Id = dsm.Id };
            // DataSource 自己的 init 属性(MinuteCeiling / DefaultContext 等)
            foreach (var kv in dsm.Properties) node.Properties[kv.Key] = kv.Value;
            if (!string.IsNullOrEmpty(dsm.DefaultContext))
                node.Properties["DefaultContext"] = dsm.DefaultContext!;

            // OutputPort → globalId 反查表:DS 所有 output port globalId 都在 OutputBindings 里。
            // 同时把 BindingId 写到 Port —— roundtrip 保留原始 binding 字符串(经 legacy alias 翻译)。
            foreach (var p in node.OutputPorts)
            {
                if (dsm.OutputBindings.TryGetValue(p.Id, out var raw))
                {
                    var gid = PortBindingValue.ExtractSingle(raw);
                    if (!string.IsNullOrEmpty(gid)) globalIdToOutput[gid] = (node.Id, p.Id);
                }
            }
            node = ApplyBindingIds(node, dsm.InputBindings, dsm.OutputBindings);
            return node;
        }

        private static Node? TryCreateTraitNode(StyleModel sm)
        {
            Type traitType;
            try { traitType = ComponentRegistry.Resolve(sm.TraitTypeName); }
            catch { Console.WriteLine($"[Graph 反序列化警告] Trait '{sm.TraitTypeName}' 未登记,忽略此 trait 节点。"); return null; }

            var node = NodeFactory.CreateNode(traitType, new HevoPoint(0, 0));
            // Preset 字符串以及其他 Property 全都按 key 写回(NodeEditorWindow 会把"Preset"特殊处理,
            // 其余键正常通过 BuildOneField 反射展开)
            if (!string.IsNullOrEmpty(sm.Preset)) node.Properties["Preset"] = sm.Preset!;
            foreach (var kv in sm.Properties) node.Properties[kv.Key] = kv.Value;
            return node;
        }

        private static Node? TryCreateFeatureNode(FeatureModel fm, Dictionary<string, (string, string)> globalIdToOutput)
        {
            Type featureType;
            try { featureType = ComponentRegistry.Resolve(fm.TypeName); }
            catch { Console.WriteLine($"[Graph 反序列化警告] Feature '{fm.TypeName}' 未登记,忽略此 feature 节点。"); return null; }

            var node = NodeFactory.CreateNode(featureType, new HevoPoint(0, 0));

            // Properties: PortBindings 里 key 是端口名,普通 init 属性也在 fm.Properties 里。
            // PortBindings 是边重建的依据,不写回 node.Properties(避免编辑器把它当普通字段编辑)。
            foreach (var kv in fm.Properties)
            {
                node.Properties[kv.Key] = kv.Value;
            }

            // Events 路由表:GraphState 的 Node 没有 Events 槽,搭车 Properties[__Events__] 透传往返。
            // GraphSerializer.ToBlueprint 反向把它取回 fm.Events,业务侧编辑器保存 draft 后双击路由仍生效。
            if (fm.Events != null && fm.Events.Count > 0)
            {
                node.Properties[FeatureModel.EventsPropertyKey] = new Dictionary<string, string>(fm.Events);
            }

            // §D2.X 多输入 + PlotFeature.SeriesOutputs:扫 PortBindings 里 "Inputs.{name}" / "SeriesOutputs.{name}"
            // 这类 nested key,反查 Feature 上 Dictionary<string, DataPort<T>> 字段方向(PortDirection.Input / Output),
            // 展开到 InputPorts 或 OutputPorts 对应一侧。否则下面 BuildFeatureInputEdges / Output 反查找不到
            // 对应 port,边丢失,画布上看起来"指标节点没接输入/输出"。
            node = AutoExpandDictPorts(node, fm, featureType);

            // OutputPort → globalId 反查表:Feature 的 OutputPort globalId 都在 OutputBindings 里。
            foreach (var op in node.OutputPorts)
            {
                if (fm.OutputBindings.TryGetValue(op.Id, out var raw))
                {
                    var gid = PortBindingValue.ExtractSingle(raw);
                    if (!string.IsNullOrEmpty(gid)) globalIdToOutput[gid] = (node.Id, op.Id);
                }
            }
            // 把 binding 字符串(经 legacy alias 翻译)写到对应 Port.BindingId,
            // GraphState 一等数据,跨 serialize/deserialize roundtrip 原样保留。
            node = ApplyBindingIds(node, fm.InputBindings, fm.OutputBindings);
            return node;
        }

        /// <summary>
        /// 把 binding 字符串(经 legacy alias 翻译)写到对应 Port.BindingId。
        /// InputPort 从 <paramref name="inputBindings"/> 读;OutputPort 从 <paramref name="outputBindings"/> 读。
        /// 数组扇入端口(IsArray)不走 BindingId —— BindingId 是单字符串容纳不了多源,数组端口靠 edges 反查兜底。
        /// </summary>
        private static Node ApplyBindingIds(Node node,
            IReadOnlyDictionary<string, object?> inputBindings,
            IReadOnlyDictionary<string, object?> outputBindings)
        {
            if ((inputBindings == null || inputBindings.Count == 0) && (outputBindings == null || outputBindings.Count == 0)) return node;

            Port[]? newInputs = null;
            for (int i = 0; i < node.InputPorts.Count; i++)
            {
                var p = node.InputPorts[i];
                if (!inputBindings!.TryGetValue(p.Id, out var raw)) continue;
                if (p.IsArray) continue;
                var gid = PortBindingValue.ExtractSingle(raw);
                if (string.IsNullOrEmpty(gid)) continue;
                var migrated = BindingIdMigrator.Migrate(gid);
                if (p.BindingId == migrated) continue;
                newInputs ??= node.InputPorts.ToArray();
                newInputs[i] = p with { BindingId = migrated };
            }

            Port[]? newOutputs = null;
            for (int i = 0; i < node.OutputPorts.Count; i++)
            {
                var p = node.OutputPorts[i];
                if (!outputBindings!.TryGetValue(p.Id, out var raw)) continue;
                var gid = PortBindingValue.ExtractSingle(raw);
                if (string.IsNullOrEmpty(gid)) continue;
                var migrated = BindingIdMigrator.Migrate(gid);
                if (p.BindingId == migrated) continue;
                newOutputs ??= node.OutputPorts.ToArray();
                newOutputs[i] = p with { BindingId = migrated };
            }

            if (newInputs == null && newOutputs == null) return node;
            return node with
            {
                InputPorts  = newInputs  ?? node.InputPorts,
                OutputPorts = newOutputs ?? node.OutputPorts,
            };
        }

        /// <summary>
        /// §D2.X 反序列化时按 InputBindings / OutputBindings 里的 nested key("Inputs.high" / "SeriesOutputs.upper" / ...)
        /// 展开节点的 child Port。按 Feature 上字段的 <see cref="PortDirection"/> 标注决定落到
        /// InputPorts 还是 OutputPorts(默认 Input)。
        /// </summary>
        private static Node AutoExpandDictPorts(Node node, FeatureModel fm, Type featureType)
        {
            // 收集 nested keys 按 parent 字段分组 —— Input / Output 两字典都要扫
            Dictionary<string, List<string>>? groups = null;
            foreach (var kv in fm.InputBindings.Concat(fm.OutputBindings))
            {
                int dot = kv.Key.IndexOf('.');
                if (dot <= 0) continue;
                var parent = kv.Key.Substring(0, dot);
                var child = kv.Key.Substring(dot + 1);
                groups ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!groups.TryGetValue(parent, out var list)) { list = new List<string>(); groups[parent] = list; }
                if (!list.Contains(child)) list.Add(child);
            }
            if (groups == null) return node;

            foreach (var (parent, children) in groups)
            {
                var pi = featureType.GetProperty(parent,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (pi == null) continue;
                var pt = pi.PropertyType;
                if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(Dictionary<,>)) continue;
                var args = pt.GetGenericArguments();
                if (args[0] != typeof(string)) continue;
                if (!args[1].IsGenericType || args[1].GetGenericTypeDefinition() != typeof(DataPort<>)) continue;
                var portDataType = args[1].GetGenericArguments()[0];

                var direction = PortMetadataRegistry.ResolveDirection(featureType, pi);
                node = direction == PortDirection.Output
                    ? NodeFactory.ExpandOutputSlots(node, parent, children, portDataType)
                    : NodeFactory.ExpandInputSlots(node, parent, children, portDataType);
            }
            return node;
        }

        // ==========================================
        //  Viewport 命中
        // ==========================================

        private static bool BlueprintReferencesViewport(ChartBlueprint blueprint)
        {
            foreach (var dsm in blueprint.DataSources)
            {
                foreach (var kv in dsm.OutputBindings)
                {
                    foreach (var id in PortBindingValue.ExtractList(kv.Value))
                        if (IsViewportId(id)) return true;
                }
            }
            foreach (var fm in blueprint.Features)
            {
                // 单端口 / 扇入数组 / 老 CSV 一并枚举,任何一根碰到 well-known id 就 return。
                foreach (var kv in fm.InputBindings.Concat(fm.OutputBindings))
                {
                    foreach (var id in PortBindingValue.ExtractList(kv.Value))
                        if (IsViewportId(id)) return true;
                }
            }
            return false;
        }

        private static bool IsViewportId(string id) =>
            id == NodeFactory.ViewportLogicalLengthId
         || id == NodeFactory.ViewportUserRangeId
         || id == NodeFactory.ViewportActiveRangeId;

        // Composite sentinel 匹配:走 [BlueprintTypeAlias] 反查,canonical "Composite" 字面值集中在
        // Composite<TItem> 类定义,framework 代码不再散布。容错 ".NET 反射格式 Composite`1" 由 helper 内部处理。
        private static bool IsCompositeSentinel(string typeName) =>
            BlueprintTypeAlias.MatchesAlias(typeName, typeof(Hevo.Charting.WorkFlow.Composite<>));

        // Composite sentinel: 从 UpstreamRefs[0] 引用的上游 DS TypeName 反推 TItem,闭合 Composite<TItem>。
        // node-wrap 模式后只剩这一条路径,inline spec 已废弃。
        private static Type? ResolveCompositeTypeForGraph(DataSourceModel dsm, ChartBlueprint blueprint)
        {
            string? firstTypeName = null;
            if (dsm.UpstreamRefs != null)
            {
                foreach (var id in dsm.UpstreamRefs)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    var refDsm = blueprint.DataSources.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
                    if (refDsm != null) { firstTypeName = refDsm.TypeName; break; }
                }
            }
            if (string.IsNullOrEmpty(firstTypeName)) return null;

            Type upstreamType;
            try { upstreamType = ComponentRegistry.Resolve(firstTypeName!); }
            catch { return null; }

            var itemType = NodeFactory.FindDataSourceItemType(upstreamType);
            return itemType == null ? null
                : typeof(Hevo.Charting.WorkFlow.Composite<>).MakeGenericType(itemType);
        }

        // 跟 BlueprintRunner.ExtractFirstString 同款多形态枚举(string[] / List<string> / JsonElement Array)。
        // 目前 dsm.UpstreamRefs 是强类型 List<string>,这个 helper 暂时无主调用但留着以备业务侧蓝图扩展。
        private static IEnumerable<string?> EnumerateStrings(object raw)
        {
            if (raw is System.Collections.IEnumerable en && raw is not string)
            {
                foreach (var item in en)
                {
                    if (item is string s) yield return s;
                    else if (item is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.String)
                        yield return el.GetString();
                }
                yield break;
            }
            if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in je.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String) yield return item.GetString();
                }
            }
        }
    }

    /// <summary>
    /// 蓝图自动布局 —— 泳道是竖向列带,从左到右依次是 数据源 → Trait → Environment 环境 → Axes 坐标轴 →
    /// Series 序列 → Interaction 交互,跟数据流方向一致;端口右出左入,所以绝大多数连线从左往右走。
    /// <para>
    /// 复用拓扑监控的分层布局 <see cref="LayeredGraphLayout"/>(参照 ELK Layered):每条泳道是一个分区,
    /// 占一段连续的列;泳道内按数据流分列,长边拆虚拟节点减少交叉,连线是绕开节点的横竖折线。
    /// 从后面泳道连回前面泳道的线(AutoScale 收 Series 的列、交互回写 Header 等)当回边,
    /// 从全部节点下方的独立走廊绕回,不穿节点、彼此不重合。
    /// </para>
    /// <para>公开给编辑器的"自动布局"按钮复用。</para>
    /// </summary>
    public static class AutoLayout
    {
        // 列与列之间留给连线竖段的宽度:太宽整图变宽、适配视图时缩得字看不清;太窄竖段挤成一团。
        // 示例图最挤的缝隙有六七根竖段,80 时间距约 10px。
        private const double ColumnGap = 80;
        private const double Left = 40;
        // 顶部留给泳道标题和图例。
        private const double Top = 100;
        private const double NodeSpacing = 28;

        // 泳道从左到右的顺序 = 分区序号。
        private static readonly FeatureCategory[] LaneOrder =
        {
            FeatureCategory.DataSource,
            FeatureCategory.Trait,
            FeatureCategory.Environment,
            FeatureCategory.Axes,
            FeatureCategory.Series,
            FeatureCategory.Interaction,
            FeatureCategory.Unknown,
        };

        /// <summary>
        /// 就地改写 <paramref name="nodes"/> 的位置(Node 是 record,with 出新引用回写),不改 <paramref name="edges"/>;
        /// 返回每根连线(按 <see cref="Edge.Id"/>)的折线,画布坐标。
        /// </summary>
        public static Dictionary<string, IReadOnlyList<HevoPoint>> Apply(List<Node> nodes, List<Edge> edges)
        {
            var routes = new Dictionary<string, IReadOnlyList<HevoPoint>>();
            if (nodes.Count == 0) return routes;

            var byId = new Dictionary<string, Node>();
            foreach (var n in nodes) byId[n.Id] = n;

            var lNodes = nodes.Select(n => new LayeredGraphLayout.Node
            {
                Id = n.Id,
                Height = n.Size.Y,
                Partition = Array.IndexOf(LaneOrder, LaneFor(n)),
                PinFirstLayer = n.Kind == NodeKind.DataSource,
            }).ToList();

            var lEdges = new List<LayeredGraphLayout.Edge>(edges.Count);
            foreach (var e in edges)
            {
                byId.TryGetValue(e.FromNodeId, out var from);
                byId.TryGetValue(e.ToNodeId, out var to);
                var fp = from?.FindPort(e.FromPortId, isInput: false);
                var tp = to?.FindPort(e.ToPortId, isInput: true);
                lEdges.Add(new LayeredGraphLayout.Edge
                {
                    Src = e.FromNodeId,
                    Tgt = e.ToNodeId,
                    // 端口相对节点顶边的偏移跟节点当前位置无关,直接拿现位置算差值。
                    SrcPortY = from != null && fp != null ? from.GetPortPosition(fp).Y - from.Position.Y : (double?)null,
                    TgtPortY = to != null && tp != null ? to.GetPortPosition(tp).Y - to.Position.Y : (double?)null,
                });
            }

            double nodeW = nodes.Max(n => n.Size.X);
            var res = LayeredGraphLayout.Compute(lNodes, lEdges, new LayeredGraphLayout.Options
            {
                Left = Left,
                Top = Top,
                // 不按窗口铺满:蓝图画布可平移缩放,列宽和高度都取最小值,内容多大就占多大。
                Width = 0,
                Height = 0,
                MaxVerticalExpand = 1,
                MinNodeWidth = nodeW,
                MaxNodeWidth = nodeW,
                MinColumnGap = ColumnGap,
                NodeSpacing = NodeSpacing,
                RouteBackEdgesNear = true,
                // 每条回绕总线上方要放一行来源标注,走廊间距得容得下。
                BackEdgeSpacing = 24,
                WeightedCycleBreaking = true,
                MergeSamePortTracks = true,
                // 长边并排横穿多列时,间距太小在 0.9 倍缩放下几乎贴在一起,看着像重合。
                DummySpacing = 18,
            });

            for (int i = 0; i < nodes.Count; i++)
            {
                if (!res.Boxes.TryGetValue(nodes[i].Id, out var b)) continue;
                // 节点比列宽里的节点框窄时左对齐,输出端口落在框右边缘以内。
                nodes[i] = nodes[i] with { Position = new HevoPoint((float)b.X, (float)b.Y) };
            }

            for (int i = 0; i < edges.Count; i++)
            {
                var r = res.EdgeRoutes[i];
                if (r == null || r.Count < 2) continue;
                var pts = r.Select(p => new HevoPoint((float)p.X, (float)p.Y)).ToList();
                // 节点比列宽窄时,首尾点要贴到端口真实位置,画布据此判断折线是否还有效。
                int fi = nodes.FindIndex(n => n.Id == edges[i].FromNodeId);
                int ti = nodes.FindIndex(n => n.Id == edges[i].ToNodeId);
                if (fi >= 0)
                {
                    var n = nodes[fi];
                    pts[0] = new HevoPoint(n.Position.X + n.Size.X, pts[0].Y);
                }
                if (ti >= 0) pts[pts.Count - 1] = new HevoPoint(nodes[ti].Position.X, pts[pts.Count - 1].Y);
                routes[edges[i].Id] = pts;
            }
            return routes;
        }

        /// <summary>整张图重排,节点位置和连线折线一起换掉。</summary>
        public static GraphState Apply(GraphState state)
        {
            var nodes = state.Nodes.ToList();
            var edges = state.Edges.ToList();
            var routes = Apply(nodes, edges);
            return state with { Nodes = nodes, EdgeRoutes = routes };
        }

        /// <summary>节点的类别泳道。NodeKind 优先(DataSource / Viewport 固定泳道),其它走 Node.Category。</summary>
        public static FeatureCategory LaneFor(Node n) => n.Kind switch
        {
            NodeKind.DataSource => FeatureCategory.DataSource,
            NodeKind.Viewport   => FeatureCategory.Environment,
            _ => n.Category == FeatureCategory.Unknown ? FeatureCategory.Environment : n.Category,
        };
    }
}
