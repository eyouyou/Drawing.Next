namespace Hevo.Charting.LowCode
{
    /// <summary>
    /// 列里"这个位置没有数据"的统一占位:double / float 用 NaN,其余类型用 default。
    /// 渲染侧(CandleLayer / LineLayer / BarLayer / AutoScale)按"非有限值 = 缺失"跳过;
    /// 用 0 占位会被当成真实价格画出来(负价 / 零价合约下 0 是合法值,不能再拿来表示缺失)。
    /// </summary>
    public static class MissingValue<T>
    {
        public static readonly T Value = (T)MissingValue.For(typeof(T))!;
    }

    public static class MissingValue
    {
        /// <summary>按运行时类型取缺失占位(蓝图反射装配用)。</summary>
        public static object? For(Type valueType)
        {
            if (valueType == typeof(double)) return double.NaN;
            if (valueType == typeof(float)) return float.NaN;
            return valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
        }
    }
}
