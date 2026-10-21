using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Hevo.Charting.LowCode.Designer.GraphViewer;
using Hevo.Charting.Samples;

namespace Hevo.Charting.Avalonia.Tests
{
    /// <summary>Avalonia 蓝图编辑器:打开示例 dashboard、看图、拖节点、断开再连回连线、撤销、运行。</summary>
    public sealed class AvaloniaDesignerTests
    {
        private sealed class Rig
        {
            public readonly AvaloniaBlueprintDesigner Designer = new();
            public readonly Window Window;

            public Rig()
            {
                SampleDashboards.RegisterComponents();
                Window = new Window { Content = Designer, Width = 1400, Height = 900 };
                Window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Null(Designer.LoadJson(SampleDashboards.KLineDashboardJson()));
                Pump();
            }

            public GraphState State => Designer.Schema.DataSource.State;

            public void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                Designer.Cell.RunFrameNow();
            }

            /// <summary>画布坐标 → 窗口坐标(经当前画布变换 + cell 在窗口里的位置)。</summary>
            public Point ToWindow(HevoPoint canvas)
            {
                var screen = State.Transform.CanvasToScreen(canvas);
                return Designer.Cell.TranslatePoint(new Point(screen.X, screen.Y), Window)!.Value;
            }

            public void Drag(Point from, Point to, int steps = 6)
            {
                Window.MouseMove(from);
                Window.MouseDown(from, MouseButton.Left);
                Pump();
                for (int i = 1; i <= steps; i++)
                {
                    Window.MouseMove(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
                    Pump();
                }
                Window.MouseUp(to, MouseButton.Left);
                Pump();
            }

            public void Click(Point at)
            {
                Window.MouseMove(at);
                Window.MouseDown(at, MouseButton.Left);
                Window.MouseUp(at, MouseButton.Left);
                Pump();
            }
        }

        [AvaloniaFact]
        public void OpenDashboardJson_ShowsGraphOfFirstCell()
        {
            var rig = new Rig();
            Assert.True(rig.State.Nodes.Count >= 5, $"节点数 {rig.State.Nodes.Count}");
            Assert.NotEmpty(rig.State.Edges);
            // 编回蓝图再校验:节点 / 连线没丢
            var blueprint = rig.Designer.ToBlueprint();
            var dry = BlueprintPreflight.DryRun(blueprint);
            Assert.Null(dry.Error);
            // 头部存回宿主无关的蓝图名,WPF 也能读
            Assert.Contains(blueprint.Features, f => f.TypeName == "UniversalHeaderFeature");
            Assert.DoesNotContain(blueprint.Features, f => f.TypeName.StartsWith("Avalonia"));
            rig.Window.Close();
            rig.Designer.Cell.Shutdown();
        }

        [AvaloniaFact]
        public void LoadBeforeShown_FitsOnceAttached()
        {
            SampleDashboards.RegisterComponents();
            var designer = new AvaloniaBlueprintDesigner();
            Assert.Null(designer.LoadJson(SampleDashboards.KLineDashboardJson()));   // 还没进可视树:不抛
            var window = new Window { Content = designer, Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(CanvasTransform.Identity, designer.Schema.DataSource.State.Transform);
            window.Close();
            designer.Cell.Shutdown();
        }

        [AvaloniaFact]
        public void DragNode_MovesIt_AndUndoRestores()
        {
            var rig = new Rig();
            var node = rig.State.Nodes.First(n => n.InputPorts.Count > 0);
            var center = new HevoPoint(node.Position.X + node.Size.X / 2, node.Position.Y + 12);
            var from = rig.ToWindow(center);
            rig.Drag(from, from + new Point(0, 90));

            var moved = rig.State.FindNode(node.Id)!;
            Assert.True(moved.Position.Y - node.Position.Y > 40, $"{node.Position} → {moved.Position}");

            rig.Designer.Schema.DataSource.Undo();
            Assert.Equal(node.Position, rig.State.FindNode(node.Id)!.Position);
            rig.Window.Close();
            rig.Designer.Cell.Shutdown();
        }

        [AvaloniaFact]
        public void ClickInputPort_Disconnects_ThenRubberBandReconnects()
        {
            var rig = new Rig();
            var edge = rig.State.Edges.First();
            var fromNode = rig.State.FindNode(edge.FromNodeId)!;
            var toNode = rig.State.FindNode(edge.ToNodeId)!;
            var outPort = fromNode.OutputPorts.First(p => p.Id == edge.FromPortId);
            var inPort = toNode.InputPorts.First(p => p.Id == edge.ToPortId);
            int before = rig.State.Edges.Count;

            // 点输入端口 = 断开连到它的线
            rig.Click(rig.ToWindow(toNode.GetPortPosition(inPort)));
            Assert.DoesNotContain(rig.State.Edges, e => e.ToNodeId == toNode.Id && e.ToPortId == inPort.Id);

            // 从输出端口拖到输入端口 = 连回去
            rig.Drag(rig.ToWindow(fromNode.GetPortPosition(outPort)), rig.ToWindow(toNode.GetPortPosition(inPort)));
            Assert.Contains(rig.State.Edges, e => e.FromNodeId == fromNode.Id && e.FromPortId == outPort.Id
                                                 && e.ToNodeId == toNode.Id && e.ToPortId == inPort.Id);
            Assert.Null(rig.State.RubberBand);
            Assert.True(rig.State.Edges.Count >= before - 1);
            rig.Window.Close();
            rig.Designer.Cell.Shutdown();
        }

        [AvaloniaFact]
        public void Run_LaunchesPreviewWindow()
        {
            var rig = new Rig();
            var result = rig.Designer.Run();
            Assert.Null(result.Error);
            Dispatcher.UIThread.RunJobs();
            foreach (var w in ((global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime?)Application.Current!.ApplicationLifetime)?.Windows ?? Array.Empty<Window>())
                if (!ReferenceEquals(w, rig.Window)) w.Close();
            rig.Window.Close();
            rig.Designer.Cell.Shutdown();
        }
    }
}
