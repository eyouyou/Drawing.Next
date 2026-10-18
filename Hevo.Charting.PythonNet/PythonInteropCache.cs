using System;
using Python.Runtime;

namespace Hevo.Charting.PythonNet
{
    /// <summary>
    /// 每次调用都要用到的 Python 对象(numpy 的 ndarray / generic 类型、threading.get_ident),由运行时取一次缓存起来,
    /// 解包返回值和记录线程 id 时不再每次 import + GetAttr。
    /// <para>
    /// 归 <see cref="PythonNetRuntime"/> 实例所有、随它 Shutdown 释放:缓存的 PyObject 只在那一次解释器生命期内有效,
    /// 做成进程级静态的话,解释器重新初始化后会拿到悬空引用。
    /// </para>
    /// 所有成员调用前必须已持 GIL。
    /// </summary>
    internal sealed class PythonInteropCache : IDisposable
    {
        private PyObject? _ndarrayType;
        private PyObject? _genericType;
        private PyObject? _empty;
        private PyObject? _ascontiguousarray;
        private PyObject? _getIdent;
        private bool _numpyResolved;

        /// <summary>numpy 不可用时返回 false(此时也不会有 ndarray / numpy 标量需要解包)。</summary>
        public bool TryGetNumpyTypes(out PyObject ndarrayType, out PyObject genericType)
        {
            if (!_numpyResolved)
            {
                _numpyResolved = true;
                try
                {
                    using var np = Py.Import("numpy");
                    _ndarrayType = np.GetAttr("ndarray");
                    _genericType = np.GetAttr("generic");
                    _empty = np.GetAttr("empty");
                    _ascontiguousarray = np.GetAttr("ascontiguousarray");
                }
                catch (PythonException)
                {
                    _ndarrayType = _genericType = _empty = _ascontiguousarray = null;
                }
            }
            ndarrayType = _ndarrayType!;
            genericType = _genericType!;
            return _ndarrayType != null;
        }

        /// <summary><c>numpy.empty</c>;numpy 不可用时抛(装箱 ndarray 入参时 numpy 是硬依赖)。</summary>
        public PyObject NumpyEmpty
        {
            get { RequireNumpy(); return _empty!; }
        }

        /// <summary><c>numpy.ascontiguousarray</c>;numpy 不可用时抛。</summary>
        public PyObject NumpyAscontiguousarray
        {
            get { RequireNumpy(); return _ascontiguousarray!; }
        }

        private void RequireNumpy()
        {
            if (!TryGetNumpyTypes(out _, out _))
                throw new InvalidOperationException("numpy 不可用,无法在 .NET 列和 ndarray 之间转换。");
        }

        /// <summary>当前线程的 Python 线程 id(等价 <c>PythonEngine.GetPythonThreadID</c>,但不每次 import threading)。</summary>
        public ulong GetPythonThreadId()
        {
            if (_getIdent == null)
            {
                using var threading = Py.Import("threading");
                _getIdent = threading.GetAttr("get_ident");
            }
            using var id = _getIdent.Invoke();
            return id.As<ulong>();
        }

        public void Dispose()
        {
            _ndarrayType?.Dispose();
            _genericType?.Dispose();
            _getIdent?.Dispose();
            _empty?.Dispose();
            _ascontiguousarray?.Dispose();
            _ndarrayType = _genericType = _getIdent = _empty = _ascontiguousarray = null;
            _numpyResolved = false;
        }
    }
}
