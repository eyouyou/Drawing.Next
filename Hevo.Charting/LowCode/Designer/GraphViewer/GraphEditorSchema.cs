namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// Graph editor schema(WPF)。装配、数据流都在核心 <see cref="GraphEditorSchemaBase"/>,这里只给 WPF 的交互 feature。
    /// <para>
    /// <b>消费者用法</b>:
    /// <code>
    /// var schema = new GraphEditorSchema();
    /// schema.DataSource.State = BuildSampleGraph();
    /// schema.DataSource.StateChanged += (n, o) =&gt; ...;
    /// schema.Interaction.NodeEditRequested += node =&gt; ...;
    /// chartHost.Schema = schema;
    /// </code>
    /// </para>
    /// </summary>
    public sealed class GraphEditorSchema : GraphEditorSchemaBase
    {
        public GraphEditorSchema()
            : base((ds, geometry) => new GraphInteractionFeature { DataSource = ds, Geometry = geometry })
        {
        }

        /// <summary>WPF 交互 feature(订阅 <see cref="GraphInteractionFeatureBase.NodeEditRequested"/> 弹 NodeEditorWindow)。</summary>
        public new GraphInteractionFeature Interaction => (GraphInteractionFeature)base.Interaction;
    }
}
