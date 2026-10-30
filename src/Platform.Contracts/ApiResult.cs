namespace Platform.Contracts;

/// <summary>
/// 统一返回包装。所有 <c>/api/v1/*</c> 端点（含错误）都返回该结构，
/// 客户端只依赖 <see cref="Code"/> 与 <see cref="Data"/>，不解析 HTTP 状态码语义。
/// </summary>
/// <typeparam name="T">成功时的负载类型。</typeparam>
public sealed record ApiResult<T>
{
    /// <summary>是否成功。<c>false</c> 时 <see cref="Data"/> 应为 <c>null</c>。</summary>
    public required bool IsSuccess { get; init; }

    /// <summary><see cref="ApiErrorCode"/> 的数值形式（便于跨语言消费，避免枚举序列化差异）。</summary>
    public required int Code { get; init; }

    /// <summary>面向人的提示；为空时客户端按 <see cref="Code"/> 本地化。</summary>
    public string? Message { get; init; }

    /// <summary>成功负载。</summary>
    public T? Data { get; init; }

    /// <summary>链路追踪号，便于与 Serilog 日志对齐。</summary>
    public string? TraceId { get; init; }
}

/// <summary><see cref="ApiResult{T}"/> 的工厂。服务端与 SDK 都通过它构造，避免各处手写字段。</summary>
public static class ApiResult
{
    /// <summary>无负载成功（例如 DELETEsucceed）。</summary>
    public static ApiResult<object?> Success(string? message = null, string? traceId = null) => new()
    {
        IsSuccess = true,
        Code = (int)ApiErrorCode.Ok,
        Message = message,
        TraceId = traceId,
    };

    /// <summary>带负载成功。</summary>
    public static ApiResult<T> Ok<T>(T data, string? message = null, string? traceId = null) => new()
    {
        IsSuccess = true,
        Code = (int)ApiErrorCode.Ok,
        Data = data,
        Message = message,
        TraceId = traceId,
    };

    /// <summary>无负载失败。</summary>
    public static ApiResult<object?> Fail(ApiErrorCode code, string? message = null, string? traceId = null) => new()
    {
        IsSuccess = false,
        Code = (int)code,
        Message = message ?? code.ToString(),
        TraceId = traceId,
    };

    /// <summary>带错误详情的失败（<paramref name="data"/> 用于字段级校验错误等场景）。</summary>
    public static ApiResult<T> Fail<T>(ApiErrorCode code, string? message = null, T? data = default, string? traceId = null) => new()
    {
        IsSuccess = false,
        Code = (int)code,
        Data = data,
        Message = message ?? code.ToString(),
        TraceId = traceId,
    };
}
