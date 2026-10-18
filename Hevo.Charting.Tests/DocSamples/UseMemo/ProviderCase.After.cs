// 01 号文档「UseMemo 报错怎么改」情况 3 改后:缓存是这个对象自己的字段,每个实例各一份。
namespace Hevo.Charting.DocSamples.UseMemo
{
    public sealed class PriceLabelProvider
    {
        private readonly int _decimals;
        private string? _format;

        public PriceLabelProvider(int decimals) => _decimals = decimals;

        // ✅ 第一次用到时建一次,之后一直复用;依赖会变的话,存下依赖、变了再重建
        public string GetFormat() => _format ??= "F" + _decimals;
    }
}
