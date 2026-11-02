using Platform.Contracts;

namespace AppFramework.Platform.Client;

/// <summary>
/// 平台调用失败：信封 <c>isSuccess=false</c>、HTTP 非 2xx，或本地校验（内容哈希）不通过。
/// </summary>
public sealed class PlatformClientException : Exception
{
    internal PlatformClientException(ApiErrorCode code, string? message, int statusCode, string? traceId)
        : base(FormatMessage(code, message, traceId))
    {
        Code = code;
        StatusCode = statusCode;
        TraceId = traceId;
    }

    /// <summary>平台错误码（分段枚举，段位即归属模块）。</summary>
    public ApiErrorCode Code { get; }

    /// <summary>HTTP 状态码；本地校验失败时为 0。</summary>
    public int StatusCode { get; }

    /// <summary>链路追踪号，报障时与服务端日志对齐。</summary>
    public string? TraceId { get; }

    private static string FormatMessage(ApiErrorCode code, string? message, string? traceId) =>
        traceId is { Length: > 0 }
            ? $"{(int)code}（{code}）：{message ?? "服务端未给出原因。"} [traceId={traceId}]"
            : $"{(int)code}（{code}）：{message ?? "服务端未给出原因。"}";
}
