using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hevo.Charting.LowCode.Designer.Converters
{
    /// <summary>
    /// UI 框架自己的颜色结构(WPF / Avalonia 的 Color 等)↔ 跟 <see cref="ColorJsonConverter"/> 同一种色码字符串。
    /// 核心不引用任何 UI 框架,按形状识别:值类型、有 byte 型 A / R / G / B 属性、有静态 FromArgb(byte, byte, byte, byte)。
    /// 业务 Feature 暴露宿主颜色类型的属性时,蓝图 JSON 照旧是 "#RRGGBB",不变成嵌套对象。
    /// </summary>
    public sealed class ArgbColorJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert != typeof(HevoColor) && typeToConvert.IsValueType && FindFromArgb(typeToConvert) != null
               && HasByte(typeToConvert, "A") && HasByte(typeToConvert, "R") && HasByte(typeToConvert, "G") && HasByte(typeToConvert, "B");

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

        private static bool HasByte(Type t, string name)
            => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var p, CanRead: true } && p == typeof(byte);

        private static MethodInfo? FindFromArgb(Type t)
        {
            var m = t.GetMethod("FromArgb", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) }, null);
            return m != null && m.ReturnType == t ? m : null;
        }

        private sealed class Converter<T> : JsonConverter<T> where T : struct
        {
            private static readonly Func<byte, byte, byte, byte, T> s_fromArgb =
                (Func<byte, byte, byte, byte, T>)FindFromArgb(typeof(T))!.CreateDelegate(typeof(Func<byte, byte, byte, byte, T>));
            private static readonly Func<T, byte> s_a = Getter("A"), s_r = Getter("R"), s_g = Getter("G"), s_b = Getter("B");

            private static Func<T, byte> Getter(string name)
            {
                var get = typeof(T).GetProperty(name)!.GetGetMethod()!;
                // 值类型实例方法的开放委托第一个参数是 ref T:包一层按值调用
                var open = (Getter<T>)get.CreateDelegate(typeof(Getter<T>));
                return v => open(ref v);
            }

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var c = ColorJsonConverter.Instance.Read(ref reader, typeof(HevoColor), options);
                return s_fromArgb(c.A, c.R, c.G, c.B);
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
                => ColorJsonConverter.Instance.Write(writer, new HevoColor(s_a(value), s_r(value), s_g(value), s_b(value)), options);
        }

        private delegate byte Getter<T>(ref T value);
    }
}
