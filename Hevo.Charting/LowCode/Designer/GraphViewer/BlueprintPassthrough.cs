using System.Text.Json;

namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    /// <summary>
    /// 画布表达不了、但载入再保存必须原样带回的蓝图内容。
    /// <para>
    /// 未登记类型的节点(插件没加载、类型改名)画不出来,但删掉它们等于编辑器替用户删了配置;
    /// Triggers / TriggerBindings 当前没有画布形态;顶层不认识的字段来自更新的版本或业务扩展。
    /// <see cref="GraphDeserializer"/> 载入时收进来,<see cref="GraphSerializer"/> 保存时原样写回。
    /// </para>
    /// </summary>
    public sealed record BlueprintPassthrough(
        IReadOnlyList<DataSourceModel> DataSources,
        IReadOnlyList<StyleModel> Traits,
        IReadOnlyList<FeatureModel> Features,
        IReadOnlyList<TriggerModel> Triggers,
        IReadOnlyList<TriggerBinding> TriggerBindings,
        IReadOnlyDictionary<string, JsonElement>? ExtensionData)
    {
        public static readonly BlueprintPassthrough Empty = new(
            Array.Empty<DataSourceModel>(),
            Array.Empty<StyleModel>(),
            Array.Empty<FeatureModel>(),
            Array.Empty<TriggerModel>(),
            Array.Empty<TriggerBinding>(),
            null);
    }
}
