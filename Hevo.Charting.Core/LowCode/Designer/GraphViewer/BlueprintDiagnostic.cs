namespace Hevo.Charting.LowCode.Designer.GraphViewer
{
    public enum BlueprintDiagnosticSeverity { Warning, Error }

    /// <summary>
    /// 蓝图诊断条目 —— Launch 之前 dry-run 校验产出。
    /// Warning 不阻止启动(运行时 silent skip 行为保留),Error 直接中断启动。
    /// </summary>
    public sealed class BlueprintDiagnostic
    {
        public BlueprintDiagnosticSeverity Severity { get; init; }
        /// <summary>稳定 code,UI / 业务可据此分类显示。</summary>
        public string Code { get; init; } = "";
        public string FeatureTypeName { get; init; } = "";
        public string PortName { get; init; } = "";
        public string Message { get; init; } = "";

        public override string ToString()
        {
            var loc = string.IsNullOrEmpty(FeatureTypeName)
                ? PortName
                : (string.IsNullOrEmpty(PortName) ? FeatureTypeName : $"{FeatureTypeName}.{PortName}");
            return string.IsNullOrEmpty(loc)
                ? $"[{Severity}] {Code}: {Message}"
                : $"[{Severity}] {Code} {loc}: {Message}";
        }
    }

    /// <summary>
    /// <see cref="BlueprintLauncher.LaunchEx"/> / <see cref="BlueprintLauncher.DryRun"/> 的结构化结果。
    /// 旧的 string? 返回值入口 (<see cref="BlueprintLauncher.Launch"/>) 仍保留,只是把 Error 拆出来。
    /// </summary>
    public sealed class BlueprintLaunchResult
    {
        public string? Error { get; init; }
        public IReadOnlyList<BlueprintDiagnostic> Diagnostics { get; init; } = Array.Empty<BlueprintDiagnostic>();
        public bool Launched => Error == null;
    }
}
