using System.Text.Json;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    /// <summary>
    /// DryRun 与运行时对"哪些属性是 handler 引用"必须同一判断。旧 DryRun 只认 string,JSON 载入的蓝图里是
    /// JsonElement,于是 MCP validate_blueprint(从 JSON 反序列化)永远报不出未注册 handler,运行时却直接抛。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class BlueprintHandlerReferenceTests
    {
        public BlueprintHandlerReferenceTests()
        {
            ComponentRegistry.Register<TestDataSource>();
            ComponentRegistry.Register<Hevo.Charting.Features.ComputeFeature>();
        }

        private const string Json = @"{
            ""DataSources"": [ { ""Id"": ""p"", ""TypeName"": ""TestDataSource"", ""OutputBindings"": { ""Value"": ""ds_v"" } } ],
            ""Features"": [ {
                ""TypeName"": ""ComputeFeature"",
                ""Properties"": { ""Compute"": ""no_such_handler"" },
                ""InputBindings"": { ""InputPort"": ""ds_v"" },
                ""OutputBindings"": { ""OutputPort"": ""out"" }
            } ]
        }";

        [Fact]
        public void DryRun_OnJsonBlueprint_ReportsUnregisteredHandler()
        {
            var bp = JsonSerializer.Deserialize<ChartBlueprint>(Json, BlueprintJsonOptions.Default)!;

            var result = BlueprintLauncher.DryRun(bp, handlers: null);

            Assert.Contains(result.Diagnostics, d =>
                d.Code == "BP_HANDLER_NOT_REGISTERED" && d.PortName == "Compute" && d.Message.Contains("no_such_handler"));
        }

        [Fact]
        public void DryRun_OnJsonBlueprint_RegisteredHandler_NoWarning()
        {
            var bp = JsonSerializer.Deserialize<ChartBlueprint>(Json.Replace("no_such_handler", "wrong_shape"), BlueprintJsonOptions.Default)!;
            var handlers = new BlueprintHandlerRegistry();
            handlers.RegisterDelegate("wrong_shape", new Action(() => { }));

            var result = BlueprintLauncher.DryRun(bp, handlers);

            Assert.DoesNotContain(result.Diagnostics, d => d.Code == "BP_HANDLER_NOT_REGISTERED");
        }
    }
}
