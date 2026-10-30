using Platform.Contracts;

namespace Platform.Kernel.Errors;

/// <summary>
/// 业务异常：服务层唯一的"预期内失败"出口。中间件会把它翻译成
/// <c>ApiResult.Fail(Code)</c> + <see cref="ApiErrorHttpStatus.Map"/> 的状态码。
/// </summary>
/// <remarks>
/// 不要用它表达编程错误（空引用、越界等）——那些应当直接冒泡成 500 并告警。
/// </remarks>
public class BusinessException : Exception
{
    /// <summary>按错误码构造；<paramref name="message"/> 为空时用错误码名兜底。</summary>
    public BusinessException(ApiErrorCode code, string? message = null, Exception? innerException = null)
        : base(message ?? code.ToString(), innerException)
    {
        Code = code;
    }

    /// <summary>业务错误码。</summary>
    public ApiErrorCode Code { get; }

    /// <summary>对应的 HTTP 状态码（由映射表唯一决定）。</summary>
    public int HttpStatus => ApiErrorHttpStatus.Map(Code);

    /// <summary>可选的字段级详情（例如校验失败时逐字段说明）。</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    /// <summary>未找到。</summary>
    public static BusinessException NotFound(ApiErrorCode code, string? message = null) => new(code, message);

    /// <summary>校验失败。</summary>
    public static BusinessException Invalid(string? message = null) => new(ApiErrorCode.ValidationFailed, message);

    /// <summary>无权限。</summary>
    public static BusinessException Forbidden(string? message = null) => new(ApiErrorCode.Forbidden, message);
}
