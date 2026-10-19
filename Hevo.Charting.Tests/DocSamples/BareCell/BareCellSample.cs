using System.Windows.Media;
using Hevo.Charting.Abstractions;
using Hevo.Charting.Core;
using Hevo.Charting.Renderers;

// 不用 Schema / Feature,直接在裸 ChartCell 上挂层、喂数据。
// 写法按业务侧红线(LangVersion 9):块级 namespace、显式 using。
namespace Hevo.Charting.DocSamples.BareCell
{
    /// <summary>
    /// 图层的"ViewModel":画一条水平线要的全部数据。喂一份新的 = 换 DataContext,图层下一帧重画。
    /// </summary>
    public sealed record LevelLineTrait(double Y, double Width, HevoPen Pen) : IVisualTrait;

    /// <summary>
    /// 图层是 View:只读本层的 Trait 录指令。必须 partial —— 生成器补上 IConsumes&lt;LevelLineTrait&gt;,
    /// 代理上的 PublishData(new LevelLineTrait(...)) 才能通过编译。
    /// </summary>
    public partial class LevelLineLayer : ChartLayer
    {
        public LevelLineLayer()
        {
            Name = "LevelLine";
            Level = ChartLayerType.Main;
        }

        protected override void OnUpdate(IVisualData data, IDrawingSink draw, WidgetBuffer widget)
        {
            var line = data.Get<LevelLineTrait>();
            if (line == null) return;
            draw.DrawLine(line.Pen, new HevoPoint(0, (float)line.Y), new HevoPoint((float)line.Width, (float)line.Y));
        }
    }

    public static class BareCellSample
    {
        /// <summary>
        /// 挂层 + 喂第一份数据。返回的代理留着:之后每次 PublishData 都会让图层下一帧重画,任何线程都可以喂。
        /// 不喂数据的图层不会画(深度睡眠,见 00 号文档 §16.1)。
        /// </summary>
        public static VisualProxy<LevelLineLayer> AddLevelLine(ChartCell cell, LevelLineLayer layer, double y)
        {
            var feed = cell.AddUnmanagedLayer(layer);
            feed.PublishData(new LevelLineTrait(y, 200, new HevoPen(new HevoSolidBrush(Colors.OrangeRed), 1)));
            return feed;
        }
    }
}
