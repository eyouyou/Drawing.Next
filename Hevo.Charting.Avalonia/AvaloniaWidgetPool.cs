using Avalonia.Controls;
using Hevo.Charting.Core;

namespace Hevo.Charting.Avalonia
{
    /// <summary>
    /// 图层专属的控件池(对应 WPF 的 LayerWidgetPool):一个图层 N 条控件指令 → N 个复用的 ContentControl,
    /// 按指令的 Bounds 摆在控件层 Canvas 上。指令里的 ViewModel 是 Avalonia 控件或任意内容。
    /// </summary>
    internal sealed class AvaloniaWidgetPool
    {
        private readonly Canvas _canvas;
        private readonly List<ContentControl> _pool = new();
        private int _activeCount;

        public AvaloniaWidgetPool(Canvas canvas) => _canvas = canvas;

        public void Sync(IReadOnlyList<WidgetCommand> commands)
        {
            int newActive = commands.Count;
            int prevActive = _activeCount;

            while (_pool.Count < newActive)
            {
                var cc = new ContentControl { IsHitTestVisible = false };
                _canvas.Children.Add(cc);
                _pool.Add(cc);
            }

            for (int i = 0; i < newActive; i++)
            {
                var cc = _pool[i];
                var cmd = commands[i];
                if (i >= prevActive) cc.IsVisible = true;
                if (!ReferenceEquals(cc.Content, cmd.ViewModel)) cc.Content = cmd.ViewModel;
                Canvas.SetLeft(cc, cmd.Bounds.X);
                Canvas.SetTop(cc, cmd.Bounds.Y);
            }

            for (int i = newActive; i < prevActive; i++)
            {
                _pool[i].IsVisible = false;
                _pool[i].Content = null; // 放掉 ViewModel 引用
            }

            _activeCount = newActive;
        }

        public void Destroy()
        {
            foreach (var cc in _pool)
            {
                cc.Content = null;
                _canvas.Children.Remove(cc);
            }
            _pool.Clear();
            _activeCount = 0;
        }
    }
}
