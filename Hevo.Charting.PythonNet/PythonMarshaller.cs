using System;
using System.Runtime.InteropServices;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// §D2.5.3 跨边界数据转换:.NET → Python(Box)。
    /// Python → .NET 方向在 <see cref="PythonNetModule"/> 里按值的实际类型解包,再由 <see cref="PythonReturnCoercion"/> 按委托返回类型放宽。
    /// <para>
    /// 当前实装是 <b>第一波 Marshal.Copy 版</b>(<see cref="ReadOnlyMemory{T}"/> ↔ numpy ndarray
    /// 走一次 memcpy)。1000 点 double ~1μs,跟"把蓝图跑起来"比是 noise。
    /// </para>
    /// <para>
    /// 后续优化(§D2.5.3+)走 <c>MemoryHandle.Pin</c> + numpy <c>ctypeslib.as_array</c> 共享底层内存,
    /// 但需要管 GC pin 生命周期 + numpy 引用计数,放到 zero-copy 迭代里再做。
    /// </para>
    /// </summary>
    internal static class PythonMarshaller
    {
        /// <summary>
        /// .NET → Python:把任意支持类型装成 PyObject。null 走 <c>None</c>。
        /// 调用方拿到 PyObject 必须自己 Dispose(GIL 内 using)。
        /// </summary>
        /// <remarks>调用前必须已经 acquire GIL。</remarks>
        /// <param name="cache">运行时的缓存;给了就不每次 import numpy。</param>
        public static PyObject Box(object? value, PythonInteropCache? cache = null)
        {
            if (value is null)
            {
                // pythonnet 3.0.x 没暴露 PyObject.None 静态字段;builtins.None 是稳妥跨版本路径。
                using var builtins = Py.Import("builtins");
                return builtins.GetAttr("None");
            }

            switch (value)
            {
                case bool b:    return new PyInt(b ? 1 : 0);   // pythonnet 没 PyBool 公开 ctor,bool→int 走 truthiness
                case int i:     return new PyInt(i);
                case long l:    return new PyInt(l);
                case double d:  return new PyFloat(d);
                case float f:   return new PyFloat(f);
                case string s:  return new PyString(s);
                case DateTime dt: return new PyInt(dt.ToUniversalTime().Ticks);

                // ReadOnlyMemory<double>:首选业务数据形态(指标计算的核心)
                case ReadOnlyMemory<double> rom_d: return ToNumpyArray(rom_d, "float64", cache);
                case Memory<double> m_d:           return ToNumpyArray<double>(m_d.Span, "float64", cache);
                case double[] arr_d:               return ToNumpyArray<double>(arr_d.AsSpan(), "float64", cache);

                case ReadOnlyMemory<int> rom_i: return ToNumpyArray(rom_i, "int32", cache);
                case Memory<int> m_i:           return ToNumpyArray<int>(m_i.Span, "int32", cache);
                case int[] arr_i:               return ToNumpyArray<int>(arr_i.AsSpan(), "int32", cache);

                // 兜底:让 pythonnet 自己反射桥接(record / 自定义 .NET 类型 → Python 通用 .NET ref)
                default:
                    return value.ToPython();
            }
        }

        // ===== ROM<T> ↔ numpy ndarray (Marshal.Copy 版) ============================

        /// <summary>
        /// 构造 <c>numpy.empty(n, dtype)</c>,把 <paramref name="span"/> 的字节 memcpy 到底层 buffer。
        /// </summary>
        private static unsafe PyObject ToNumpyArray<T>(ReadOnlySpan<T> span, string dtype, PythonInteropCache? cache) where T : unmanaged
        {
            using var len = new PyInt(span.Length);
            using var dt = new PyString(dtype);
            // np.empty(n, dtype) → ndarray
            PyObject arr;
            if (cache != null)
            {
                arr = cache.NumpyEmpty.Invoke(len, dt);
            }
            else
            {
                using var np = Py.Import("numpy");
                arr = np.InvokeMethod("empty", new PyObject[] { len, dt });
            }

            if (span.Length == 0) return arr;

            // ndarray.ctypes.data 拿到底层 buffer 的 native ptr,memcpy 进去。
            using var ctypes = arr.GetAttr("ctypes");
            using var dataAttr = ctypes.GetAttr("data");
            long dst = dataAttr.As<long>();

            int byteCount = sizeof(T) * span.Length;
            fixed (T* src = span)
            {
                Buffer.MemoryCopy(src, (void*)(IntPtr)dst, byteCount, byteCount);
            }
            return arr;
        }

        private static unsafe PyObject ToNumpyArray<T>(ReadOnlyMemory<T> mem, string dtype, PythonInteropCache? cache) where T : unmanaged
            => ToNumpyArray(mem.Span, dtype, cache);
    }
}
