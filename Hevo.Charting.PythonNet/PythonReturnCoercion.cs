using System;
using System.Globalization;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// Python 返回值(已解包成纯 .NET 对象)按委托声明的返回类型做数值放宽。
    /// <para>
    /// 解包只认 Python 自己的类型:int → long、float → double、int64 ndarray → ROM&lt;long&gt;。
    /// 分析师写 <c>-&gt; double</c> 却 <c>return len(x)</c>、写 <c>-&gt; int</c> 却返回 <c>np.int64</c> 都很常见,
    /// 直接拆箱会抛 InvalidCastException,所以在这里按目标类型转换;转换会丢信息(小数转整数)时仍然报错。
    /// </para>
    /// </summary>
    internal static class PythonReturnCoercion
    {
        public static object? Coerce(object? raw, Type target)
        {
            if (target == typeof(object) || target == typeof(void)) return raw;

            var underlying = Nullable.GetUnderlyingType(target);
            if (raw == null)
            {
                // None 对浮点返回值按缺失值处理,跟指标列里 NaN 表示缺失一致
                if (target == typeof(double)) return double.NaN;
                if (target == typeof(float)) return float.NaN;
                if (!target.IsValueType || underlying != null) return null;
                throw new InvalidCastException($"Python 返回 None,无法转换为 {target.Name}。");
            }
            if (underlying != null) return Coerce(raw, underlying);
            if (target.IsInstanceOfType(raw)) return raw;

            if (IsNumeric(target) && IsNumeric(raw.GetType()))
                return ConvertNumber(raw, target);

            if (target == typeof(DateTime) && (raw is long || raw is int))
                return new DateTime(Convert.ToInt64(raw, CultureInfo.InvariantCulture), DateTimeKind.Utc);

            if (target == typeof(ReadOnlyMemory<double>))
            {
                var col = ToDoubleColumn(raw);
                if (col != null) return new ReadOnlyMemory<double>(col);
            }

            throw new InvalidCastException(
                $"Python 返回值类型 {raw.GetType().Name} 无法转换为签名声明的 {target.Name}。");
        }

        private static bool IsNumeric(Type t)
            => t == typeof(double) || t == typeof(float) || t == typeof(long) || t == typeof(int)
               || t == typeof(short) || t == typeof(byte) || t == typeof(bool) || t == typeof(decimal);

        private static object ConvertNumber(object raw, Type target)
        {
            if (target != typeof(double) && target != typeof(float) && target != typeof(decimal) && target != typeof(bool)
                && (raw is double || raw is float))
            {
                double d = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d)
                    throw new InvalidCastException($"Python 返回 {d.ToString(CultureInfo.InvariantCulture)},不是整数,不能转换为 {target.Name}。");
            }
            return Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
        }

        private static double[]? ToDoubleColumn(object raw)
        {
            switch (raw)
            {
                case ReadOnlyMemory<float> f: { var s = f.Span; var a = new double[s.Length]; for (int i = 0; i < a.Length; i++) a[i] = s[i]; return a; }
                case ReadOnlyMemory<long> l:  { var s = l.Span; var a = new double[s.Length]; for (int i = 0; i < a.Length; i++) a[i] = s[i]; return a; }
                case ReadOnlyMemory<int> n:   { var s = n.Span; var a = new double[s.Length]; for (int i = 0; i < a.Length; i++) a[i] = s[i]; return a; }
                case double[] d: return d;
                case object?[] list:
                {
                    // Python list 解包成 object?[];全是数(None 记 NaN)才当列
                    var a = new double[list.Length];
                    for (int i = 0; i < a.Length; i++)
                    {
                        var e = list[i];
                        if (e == null) a[i] = double.NaN;
                        else if (IsNumeric(e.GetType())) a[i] = Convert.ToDouble(e, CultureInfo.InvariantCulture);
                        else return null;
                    }
                    return a;
                }
            }
            return null;
        }
    }
}
