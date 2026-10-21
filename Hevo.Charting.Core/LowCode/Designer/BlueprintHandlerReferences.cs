using System.Reflection;
using System.Text.Json;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// "Feature 的某个属性是不是 handler 引用"的唯一判断。运行时装配和 DryRun 都用它 ——
    /// 两边各写一份时 DryRun 只认 string,而 JSON 载入的蓝图里是 JsonElement,MCP 校验永远报不出未注册 handler。
    /// 做成单条判断而不是迭代器:DryRun 每个 Feature 都要过一遍,迭代器对象的分配会让 DryRun 明显变重。
    /// </summary>
    internal static class BlueprintHandlerReferences
    {
        /// <summary>
        /// <paramref name="value"/> 是 Delegate 类型属性上用名字引用的 handler 时返回 true。程序化构造的蓝图给 string,
        /// JSON 反序列化后的 <c>Dictionary&lt;string, object?&gt;</c> 给 JsonElement(String),两种都认。
        /// </summary>
        internal static bool TryGet(Type featureType, string key, object? value,
            out string handlerName, out PropertyInfo property)
        {
            handlerName = null!;
            property = null!;
            var name = value switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
                _ => null,
            };
            if (string.IsNullOrEmpty(name)) return false;
            var pi = featureType.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi == null || !typeof(Delegate).IsAssignableFrom(pi.PropertyType)) return false;
            handlerName = name!;
            property = pi;
            return true;
        }
    }
}
