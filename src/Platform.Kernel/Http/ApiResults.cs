using Microsoft.AspNetCore.Http;
using Platform.Contracts;
using Platform.Kernel.Errors;

namespace Platform.Kernel.Http;

/// <summary>
/// 端点返回的统一入口。用它们代替 <c>Results.Ok</c> / <c>Results.BadRequest</c>，
/// 保证"HTTP 码由 <see cref="ApiErrorHttpStatus"/> 唯一决定"这条约束不被绕过。
/// </summary>
public static class ApiResults
{
    /// <summary>成功并带负载。</summary>
    public static IResult Ok<T>(HttpContext context, T data, string? message = null) =>
        Results.Json(ApiResult.Ok(data, message, context.TraceIdentifier));

    /// <summary>成功但无负载。</summary>
    public static IResult Success(HttpContext context, string? message = null) =>
        Results.Json(ApiResult.Success(message, context.TraceIdentifier));

    /// <summary>业务失败（状态码自动映射）。</summary>
    public static IResult Fail(HttpContext context, ApiErrorCode code, string? message = null) =>
        Results.Json(
            ApiResult.Fail(code, message, context.TraceIdentifier),
            statusCode: ApiErrorHttpStatus.Map(code));

    /// <summary>把已有 <see cref="ApiResult{T}"/> 转成 HTTP 结果（补齐 traceId）。</summary>
    public static IResult From<T>(HttpContext context, ApiResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var payload = result.TraceId is null ? result with { TraceId = context.TraceIdentifier } : result;
        var status = payload.IsSuccess
            ? StatusCodes.Status200OK
            : ApiErrorHttpStatus.Map((ApiErrorCode)payload.Code);
        return Results.Json(payload, statusCode: status);
    }

    /// <summary>把业务异常转成 HTTP 结果。</summary>
    public static IResult FromException(HttpContext context, BusinessException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var data = exception.Details is { Count: > 0 } ? (object)exception.Details : null;
        return Results.Json(
            ApiResult.Fail<object?>(exception.Code, exception.Message, data, context.TraceIdentifier),
            statusCode: exception.HttpStatus);
    }
}
