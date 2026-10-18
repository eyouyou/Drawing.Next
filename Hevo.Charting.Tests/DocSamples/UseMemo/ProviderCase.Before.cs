using Hevo.Charting.Core;

// 01 号文档「UseMemo 报错怎么改」情况 3 改前:不在 Feature 里的对象借 ctx 存缓存(HEVO008)。
// 这个文件不参与 DocSamples 编译,由 HookAnalyzerTests 拿来验证分析器会报错。
namespace Hevo.Charting.DocSamples.UseMemo.Before
{
    public sealed class PriceLabelProvider
    {
        private readonly int _decimals;

        public PriceLabelProvider(int decimals) => _decimals = decimals;

        // ❌ 两个 PriceLabelProvider 实例(比如左右两根轴)共用下面这一行的缓存,
        //    小数位交替 → 每次都重建格式串
        public string GetFormat(FeatureContext ctx)
            => ctx.UseMemo(_decimals, static d => "F" + d).Value;
    }
}
