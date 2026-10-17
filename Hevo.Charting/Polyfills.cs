// README §2:核心代码需能用 .NET 5 SDK 5.0.408 重编。
// net6+ BCL 自带的编译器识别型 attribute 在 net5 下补一份 internal 等价物;
// net6+ 目标走 BCL 版本,本文件编译为空。

#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// net5 polyfill:编译器按名字识别,语义同 .NET 6 BCL 的 <c>CallerArgumentExpressionAttribute</c>
    /// (<c>FeatureContext.UsePort</c> / <c>UsePorts</c> 用它拿 caller 端口表达式)。
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}
#endif
