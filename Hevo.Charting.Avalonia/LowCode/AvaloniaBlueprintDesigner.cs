using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Hevo.Charting.LowCode.Designer;
using Hevo.Charting.LowCode.Designer.Converters;
using Hevo.Charting.LowCode.Designer.GraphViewer;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// Avalonia 下的蓝图编辑器(对应 WPF LowCodeDemo 里的蓝图画布):打开蓝图 / dashboard JSON、看图、拖节点、连线、
    /// 撤销重做、自动布局、保存、运行。图模型(GraphState)、序列化、渲染图层、交互逻辑都在核心,跟 WPF 编辑器同一份。
    /// <para>
    /// 跟 WPF 编辑器的差别:没有节点属性编辑窗(双击节点只在状态栏显示节点信息,事件 <see cref="NodeEditRequested"/> 留给宿主)、
    /// 没有组件选择器(加节点)和 handler 选择器、没有 dashboard 多 cell 工作区(dashboard JSON 只能挑一个 cell 编辑)。
    /// </para>
    /// </summary>
    public sealed class AvaloniaBlueprintDesigner : DockPanel
    {
        private static readonly FilePickerFileType JsonType = new("蓝图 JSON") { Patterns = new[] { "*.json" } };

        private readonly ComboBox _cellPicker = new() { MinWidth = 120, IsVisible = false };
        private readonly TextBlock _status = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
        };
        private Dashboard? _dashboard;

        public AvaloniaBlueprintDesigner()
        {
            GraphViewerBootstrap.Initialize();
            Schema = new AvaloniaGraphEditorSchema();
            Cell = new AvaloniaChartCell { Template = Schema };
            Schema.Interaction.NodeEditRequested += node =>
            {
                _status.Text = $"节点 {node.Title}({node.TypeName})—— 属性编辑窗只有 WPF 版";
                NodeEditRequested?.Invoke(node);
            };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6) };
            bar.Children.Add(Button("打开…", async () => await OpenAsync()));
            bar.Children.Add(Button("保存…", async () => await SaveAsync()));
            bar.Children.Add(_cellPicker);
            bar.Children.Add(Button("自动布局", () => { Schema.DataSource.ApplyUserEdit(AutoLayout.Apply); return Task.CompletedTask; }));
            bar.Children.Add(Button("适配视图", () => { Schema.Interaction.FitToContent(); return Task.CompletedTask; }));
            bar.Children.Add(Button("撤销", () => { Schema.DataSource.Undo(); return Task.CompletedTask; }));
            bar.Children.Add(Button("重做", () => { Schema.DataSource.Redo(); return Task.CompletedTask; }));
            bar.Children.Add(Button("▶ 运行", () => { Run(); return Task.CompletedTask; }));
            bar.Children.Add(_status);
            SetDock(bar, Dock.Top);
            Children.Add(bar);
            Children.Add(Cell);

            _cellPicker.SelectionChanged += (_, _) =>
            {
                if (_dashboard != null && _cellPicker.SelectedIndex >= 0 && _cellPicker.SelectedIndex < _dashboard.Cells.Count)
                    LoadBlueprint(_dashboard.Cells[_cellPicker.SelectedIndex].Blueprint);
            };
        }

        /// <summary>画布 schema(状态源 <c>Schema.DataSource</c>、交互 <c>Schema.Interaction</c>)。</summary>
        public AvaloniaGraphEditorSchema Schema { get; }

        /// <summary>承载画布的 cell。不再使用时调 <see cref="AvaloniaChartCell.Shutdown"/>。</summary>
        public AvaloniaChartCell Cell { get; }

        /// <summary>节点被双击(宿主可以在这里弹自己的属性编辑器)。</summary>
        public event Action<Node>? NodeEditRequested;

        /// <summary>把蓝图摊到画布上(节点位置按核心的自动布局排)。</summary>
        public void LoadBlueprint(ChartBlueprint blueprint)
        {
            Schema.DataSource.State = GraphDeserializer.FromBlueprint(blueprint);
            Schema.Interaction.FitToContent();
            Report(BlueprintPreflight.DryRun(ToBlueprint()));
        }

        /// <summary>
        /// 读 JSON:单张蓝图直接打开;dashboard(顶层有 Cells)列出 cell 供挑选,先打开第一个。
        /// 解析失败返回错误串,成功返回 null。
        /// </summary>
        public string? LoadJson(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Cells", out _))
                    {
                        var dashboard = JsonSerializer.Deserialize<Dashboard>(json, BlueprintJsonOptions.Default);
                        if (dashboard == null || dashboard.Cells.Count == 0) return "dashboard 里没有 cell。";
                        _dashboard = dashboard;
                        _cellPicker.ItemsSource = dashboard.Cells.Select(c => $"{c.Id}({c.Role})").ToList();
                        _cellPicker.IsVisible = true;
                        _cellPicker.SelectedIndex = 0;
                        LoadBlueprint(dashboard.Cells[0].Blueprint);
                        return null;
                    }
                }
                var blueprint = JsonSerializer.Deserialize<ChartBlueprint>(json, BlueprintJsonOptions.Default);
                if (blueprint == null) return "蓝图 JSON 为空。";
                _dashboard = null;
                _cellPicker.IsVisible = false;
                LoadBlueprint(blueprint);
                return null;
            }
            catch (JsonException ex)
            {
                return "JSON 解析失败:" + ex.Message;
            }
        }

        /// <summary>画布当前内容编译回蓝图(连线反推成端口绑定)。</summary>
        public ChartBlueprint ToBlueprint() => GraphSerializer.ToBlueprint(Schema.DataSource.State);

        /// <summary>按当前画布跑一张预览图(弹窗)。</summary>
        public BlueprintLaunchResult Run()
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var result = AvaloniaBlueprintLauncher.LaunchEx(ToBlueprint(), owner);
            Report(result);
            return result;
        }

        private void Report(BlueprintLaunchResult result)
        {
            _status.Text = result.Error != null
                ? "❌ " + result.Error
                : result.Diagnostics.Count == 0 ? "✓ 校验通过" : $"⚠ {result.Diagnostics.Count} 条诊断(详见控制台)";
            foreach (var d in result.Diagnostics) Console.WriteLine(d);
        }

        private async Task OpenAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开蓝图 / dashboard JSON",
                AllowMultiple = false,
                FileTypeFilter = new[] { JsonType },
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var error = LoadJson(await reader.ReadToEndAsync());
            if (error != null) _status.Text = "❌ " + error;
        }

        private async Task SaveAsync()
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存蓝图 JSON",
                DefaultExtension = "json",
                SuggestedFileName = "blueprint.json",
                FileTypeChoices = new[] { JsonType },
            });
            if (file == null) return;
            var json = JsonSerializer.Serialize(ToBlueprint(), BlueprintJsonOptions.Default);
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            _status.Text = "已保存 " + file.Name;
        }

        private static Button Button(string text, Func<Task> onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3) };
            b.Click += async (_, _) => await onClick();
            return b;
        }
    }
}
