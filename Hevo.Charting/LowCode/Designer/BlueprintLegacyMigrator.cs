using System.Reflection;
using System.Text.Json;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.LowCode.Designer
{
    /// <summary>
    /// 旧格式字段的迁移入口。模型改形后,旧 JSON 里的字段反序列化时默认被静默丢掉 —— 焊接没了、图黑屏还不报错。
    /// 各模型用 <c>[JsonExtensionData]</c> 接住不认识的字段,这里把能无损翻译的迁到新字段,翻不了的报 Error,
    /// 其余不认识的报 Warning(多半是拼错)。
    /// <para>
    /// 运行时(<see cref="BlueprintRunner"/>)、DryRun(<see cref="BlueprintLauncher.DryRun"/>)、编辑器载入
    /// (<see cref="GraphDeserializer.FromBlueprint"/>)都先走这里,三处看到的是同一份蓝图。
    /// 迁移原地改写传入的蓝图,已迁移的字段从 ExtensionData 移除,重复调用无副作用。
    /// </para>
    /// </summary>
    public static class BlueprintLegacyMigrator
    {
        private static readonly string[] _dsMappingFields = { "ScalarMappings", "VectorMappings" };

        public const string CodeMigrated     = "BP_LEGACY_FIELD_MIGRATED";
        public const string CodeUnmigratable = "BP_LEGACY_FIELD_UNMIGRATABLE";
        public const string CodeUnknown      = "BP_UNKNOWN_FIELD";

        /// <summary>迁移旧字段,返回迁移记录与无法处理的字段诊断。</summary>
        public static IReadOnlyList<BlueprintDiagnostic> Migrate(ChartBlueprint blueprint)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));
            // 绝大多数蓝图没有任何不认识的字段:直接返回,DryRun / 装配路径不为迁移多付分配。
            if (!HasExtensionData(blueprint)) return Array.Empty<BlueprintDiagnostic>();
            var diags = new List<BlueprintDiagnostic>();

            MigrateTopLevelDataSource(blueprint, diags);
            foreach (var dsm in blueprint.DataSources) MigrateDataSource(dsm, diags);
            foreach (var fm in blueprint.Features) MigrateFeature(fm, diags);

            ReportUnknown(blueprint.ExtensionData, "ChartBlueprint", diags);
            foreach (var m in blueprint.DataSources) ReportUnknown(m.ExtensionData, $"DataSource '{m.Id}'", diags);
            foreach (var m in blueprint.Features) ReportUnknown(m.ExtensionData, $"Feature {m.TypeName}", diags);
            foreach (var m in blueprint.InitialTraits) ReportUnknown(m.ExtensionData, $"Trait {m.TraitTypeName}", diags);
            foreach (var m in blueprint.Triggers) ReportUnknown(m.ExtensionData, $"Trigger '{m.Id}'", diags);
            foreach (var m in blueprint.TriggerBindings) ReportUnknown(m.ExtensionData, $"TriggerBinding '{m.TriggerId}'", diags);
            foreach (var m in blueprint.Cascades) ReportUnknown(m.ExtensionData, $"Cascade {m.FromDataSourceId}->{m.ToDataSourceId}", diags);

            return diags;
        }

        private static bool HasExtensionData(ChartBlueprint bp)
        {
            if (bp.ExtensionData is { Count: > 0 }) return true;
            foreach (var m in bp.DataSources) if (m.ExtensionData is { Count: > 0 }) return true;
            foreach (var m in bp.Features) if (m.ExtensionData is { Count: > 0 }) return true;
            foreach (var m in bp.InitialTraits) if (m.ExtensionData is { Count: > 0 }) return true;
            foreach (var m in bp.Triggers) if (m.ExtensionData is { Count: > 0 }) return true;
            foreach (var m in bp.TriggerBindings) if (m.ExtensionData is { Count: > 0 }) return true;
            foreach (var m in bp.Cascades) if (m.ExtensionData is { Count: > 0 }) return true;
            return false;
        }

        /// <summary>装配期入口:迁移后仍有翻不了的旧字段就抛,不带着残缺的蓝图继续跑。</summary>
        internal static void MigrateOrThrow(ChartBlueprint blueprint)
        {
            var diags = Migrate(blueprint);
            if (diags.Count == 0) return;
            var errors = diags.Where(d => d.Severity == BlueprintDiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException("[Hevo 蓝图配置错] " + string.Join(";", errors.Select(e => e.Message)));
        }

        // 顶层单数 DataSource → DataSources[0]。新旧同时存在时分不清以谁为准,报错而不是挑一个丢另一个。
        private static void MigrateTopLevelDataSource(ChartBlueprint bp, List<BlueprintDiagnostic> diags)
        {
            if (!TryTake(bp.ExtensionData, "DataSource", out var raw, peekOnly: true)) return;
            if (raw.ValueKind == JsonValueKind.Null) { TryTake(bp.ExtensionData, "DataSource", out _); return; }

            if (bp.DataSources.Count > 0 || raw.ValueKind != JsonValueKind.Object)
            {
                diags.Add(Error(CodeUnmigratable, "",
                    "旧字段 DataSource 与 DataSources 同时出现(或不是对象),无法判断以哪个为准。请把它并入 DataSources 后删掉。"));
                return;
            }

            var dsm = JsonSerializer.Deserialize<DataSourceModel>(raw.GetRawText(), BlueprintJsonOptions.Default)!;
            if (string.IsNullOrEmpty(dsm.Id)) dsm.Id = "primary";
            bp.DataSources.Add(dsm);
            TryTake(bp.ExtensionData, "DataSource", out _);
            diags.Add(Migrated("", $"旧字段 DataSource 已迁到 DataSources(Id='{dsm.Id}'),重新保存后消失。"));
        }

        // ScalarMappings / VectorMappings(字段名 → portId)就是数据源的输出端口绑定;
        // 数据源上的 PortBindings 按端口方向拆。已有新字段同名键时以新字段为准。
        private static void MigrateDataSource(DataSourceModel dsm, List<BlueprintDiagnostic> diags)
        {
            foreach (var legacy in _dsMappingFields)
            {
                if (!TryTake(dsm.ExtensionData, legacy, out var raw)) continue;
                if (raw.ValueKind == JsonValueKind.Object)
                    foreach (var p in raw.EnumerateObject())
                        if (!dsm.OutputBindings.ContainsKey(p.Name)) dsm.OutputBindings[p.Name] = ToBindingValue(p.Value);
                diags.Add(Migrated(dsm.TypeName, $"DataSource '{dsm.Id}' 的旧字段 {legacy} 已迁到 OutputBindings。"));
            }

            if (TryTake(dsm.ExtensionData, "PortBindings", out var pb))
            {
                Type? dsType = TryResolve(dsm.TypeName);
                SplitByDirection(pb, dsType, dsm.InputBindings, dsm.OutputBindings, defaultOutput: true);
                diags.Add(Migrated(dsm.TypeName, $"DataSource '{dsm.Id}' 的旧字段 PortBindings 已按端口方向拆到 InputBindings / OutputBindings。"));
            }
        }

        private static void MigrateFeature(FeatureModel fm, List<BlueprintDiagnostic> diags)
        {
            if (!TryTake(fm.ExtensionData, "PortBindings", out var pb, peekOnly: true)) return;

            // 方向只有拿到 Feature 类型才知道;类型都解析不了时留在 ExtensionData,让调用方看到错误。
            var featureType = TryResolve(fm.TypeName);
            if (featureType == null)
            {
                diags.Add(Error(CodeUnmigratable, fm.TypeName,
                    $"Feature {fm.TypeName} 的旧字段 PortBindings 需要按端口方向拆分,但类型未登记,无法迁移。"));
                return;
            }

            TryTake(fm.ExtensionData, "PortBindings", out _);
            SplitByDirection(pb, featureType, fm.InputBindings, fm.OutputBindings, defaultOutput: false);
            diags.Add(Migrated(fm.TypeName, $"Feature {fm.TypeName} 的旧字段 PortBindings 已按端口方向拆到 InputBindings / OutputBindings。"));
        }

        // 键形如 "DataPort" 或 nested "Inputs.high":取父字段名查属性方向。查不到属性时用调用方给的默认方向
        // (数据源的输出键多半是 TItem 行字段,不是 DataPort 属性)。
        private static void SplitByDirection(JsonElement raw, Type? ownerType,
            Dictionary<string, object?> inputs, Dictionary<string, object?> outputs, bool defaultOutput)
        {
            if (raw.ValueKind != JsonValueKind.Object) return;
            foreach (var p in raw.EnumerateObject())
            {
                var parent = p.Name;
                int dot = parent.IndexOf('.');
                if (dot > 0) parent = parent.Substring(0, dot);

                bool isOutput = defaultOutput;
                var prop = ownerType?.GetProperty(parent, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null && ownerType != null)
                    isOutput = PortMetadataRegistry.ResolveDirection(ownerType, prop) == PortDirection.Output;

                var target = isOutput ? outputs : inputs;
                if (!target.ContainsKey(p.Name)) target[p.Name] = ToBindingValue(p.Value);
            }
        }

        private static void ReportUnknown(Dictionary<string, JsonElement>? ext, string owner, List<BlueprintDiagnostic> diags)
        {
            if (ext == null) return;
            foreach (var key in ext.Keys)
                diags.Add(new BlueprintDiagnostic
                {
                    Severity = BlueprintDiagnosticSeverity.Warning,
                    Code = CodeUnknown,
                    Message = $"{owner} 有不认识的字段 '{key}',运行时忽略(拼错?旧版本字段?)。",
                });
        }

        private static object? ToBindingValue(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Array  => v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToList(),
            _                    => v.Clone(),
        };

        // 大小写不敏感:BlueprintJsonOptions 开了 PropertyNameCaseInsensitive,旧 JSON 的键大小写不一定统一。
        private static bool TryTake(Dictionary<string, JsonElement>? ext, string name, out JsonElement value, bool peekOnly = false)
        {
            value = default;
            if (ext == null) return false;
            foreach (var kv in ext)
            {
                if (!string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                value = kv.Value;
                if (!peekOnly) ext.Remove(kv.Key);
                return true;
            }
            return false;
        }

        private static Type? TryResolve(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            try { return ComponentRegistry.Resolve(typeName); }
            catch { return null; }
        }

        private static BlueprintDiagnostic Migrated(string typeName, string message) => new BlueprintDiagnostic
        {
            Severity = BlueprintDiagnosticSeverity.Warning,
            Code = CodeMigrated,
            FeatureTypeName = typeName,
            Message = message,
        };

        private static BlueprintDiagnostic Error(string code, string typeName, string message) => new BlueprintDiagnostic
        {
            Severity = BlueprintDiagnosticSeverity.Error,
            Code = code,
            FeatureTypeName = typeName,
            Message = message,
        };
    }
}
