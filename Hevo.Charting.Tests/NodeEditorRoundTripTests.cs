using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Xunit;

namespace Hevo.Charting.Tests
{
    public enum EditorProbeMode { Alpha, Beta, Gamma }

    public sealed record EditorProbeGroup(int Count, string Label);

    /// <summary>编辑器往返探针:覆盖 bool / enum / Color / 数值 / 配置组这几类会被 JsonElement 卡住的字段。</summary>
    public sealed class EditorProbeFeature
    {
        public bool Enabled { get; init; }
        public EditorProbeMode Mode { get; init; } = EditorProbeMode.Alpha;
        public Color Stroke { get; init; } = Colors.Black;
        public double Thickness { get; init; } = 1.0;
        public EditorProbeGroup Group { get; init; } = new EditorProbeGroup(1, "a");
    }

    /// <summary>
    /// 属性编辑器打开再点"确定",不能把从 JSON 载入的属性写坏。旧实现里 JsonElement 不是 bool / enum / Color,
    /// 控件按"无值"显示,确定时把 true 写成 false、把 enum / Color 整个删掉。
    /// </summary>
    [Collection(nameof(BlueprintCollection))]
    public sealed class NodeEditorRoundTripTests
    {
        public NodeEditorRoundTripTests()
        {
            ComponentRegistry.Register<EditorProbeFeature>();
        }

        private static void RunOnSta(Action action)
        {
            Exception? caught = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (caught != null) throw new Xunit.Sdk.XunitException(caught.ToString());
        }

        private static Node MakeNode(string json)
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
            return new Node("n1", nameof(EditorProbeFeature), "probe", NodeKind.Feature,
                new HevoPoint(0, 0), new HevoPoint(100, 60),
                Array.Empty<Port>(), Array.Empty<Port>(), props);
        }

        private static EditorProbeFeature Inject(Dictionary<string, object?> props)
        {
            var f = new EditorProbeFeature();
            SmartActivator.InjectProperties(f, props);
            return f;
        }

        [Fact]
        public void ConfirmWithoutEdits_KeepsJsonLoadedValues() => RunOnSta(() =>
        {
            var node = MakeNode(
                "{\"Enabled\":true,\"Mode\":\"Gamma\",\"Stroke\":\"#FF112233\",\"Thickness\":2.5," +
                "\"Group\":{\"Count\":7,\"Label\":\"z\"}}");

            var window = new NodeEditorWindow(node);
            var result = window.BuildResult();

            var f = Inject(result.Properties);
            Assert.True(f.Enabled);
            Assert.Equal(EditorProbeMode.Gamma, f.Mode);
            Assert.Equal(Color.FromArgb(0xFF, 0x11, 0x22, 0x33), f.Stroke);
            Assert.Equal(2.5, f.Thickness);
            Assert.Equal(new EditorProbeGroup(7, "z"), f.Group);
        });

        [Fact]
        public void Editor_ShowsTypedValues_AndWritesOnlyEditedField() => RunOnSta(() =>
        {
            var node = MakeNode("{\"Enabled\":true,\"Mode\":\"Gamma\"}");
            var window = new NodeEditorWindow(node);

            var check = Descendants(window).OfType<System.Windows.Controls.CheckBox>().Single();
            var combo = Descendants(window).OfType<System.Windows.Controls.ComboBox>().Single();
            Assert.True(check.IsChecked);                       // JsonElement true 显示成勾选
            Assert.Equal(EditorProbeMode.Gamma, combo.SelectedItem);

            check.IsChecked = false;
            var result = window.BuildResult();

            Assert.Equal(false, result.Properties["Enabled"]);
            Assert.IsType<JsonElement>(result.Properties["Mode"]);   // 没动的字段保留原始条目
            Assert.Equal(EditorProbeMode.Gamma, Inject(result.Properties).Mode);
        });

        private static IEnumerable<object> Descendants(object root)
        {
            if (root is not System.Windows.DependencyObject d) yield break;
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(d))
            {
                yield return child;
                foreach (var g in Descendants(child)) yield return g;
            }
        }

        [Fact]
        public void ConfirmWithoutEdits_DoesNotAddKeysThatWereAbsent() => RunOnSta(() =>
        {
            // 没配的字段保持没配 —— 否则编辑器会把运行时默认值固化进 JSON,以后改默认值不再生效。
            var node = MakeNode("{\"Enabled\":true}");

            var result = new NodeEditorWindow(node).BuildResult();

            Assert.Equal(new[] { "Enabled" }, result.Properties.Keys.OrderBy(k => k).ToArray());
        });
    }
}
