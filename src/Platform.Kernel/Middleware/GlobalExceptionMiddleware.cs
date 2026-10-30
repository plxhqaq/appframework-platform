using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Contracts;
using Platform.Kernel.Errors;

// CA1848/CA1873：异常路径不是热路径，用源生成日志的收益低于可读性损失
#pragma warning disable CA1848, CA1873

namespace Platform.Kernel.Middleware;

/// <summary>
/// 全局异常中间件：把预期内失败（<see cref="BusinessException"/>）与意外异常统一翻译成
/// <see cref="ApiResult{T}"/>，避免每个端点各写 try/catch 造成返回结构不一致。
/// </summary>
/// <remarks>
/// 约束：
/// <list type="bullet">
/// <item>响应已开始写出时不再改写（否则会破坏分块下载/流式响应）；</item>
/// <item>500 只在 Development 暴露异常消息，生产环境只给错误码与 traceId；</item>
/// <item>客户端主动断开（<c>RequestAborted</c>）不算错误，不写响应体、不告警。</item>
/// </list>
/// </remarks>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly bool _exposeDetails;

    /// <summary>构造。</summary>
    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _exposeDetails = environment.IsDevelopment();
    }

    /// <summary>管道调用。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开：不写响应体，也不算失败
            _logger.LogDebug("请求被客户端取消：{Method} {Path}", context.Request.Method, context.Request.Path);
        }
        catch (BusinessException exception)
        {
            _logger.LogInformation(
                "业务失败 {Code}({Status})：{Method} {Path} - {Message}",
                exception.Code,
                exception.HttpStatus,
                context.Request.Method,
                context.Request.Path,
                exception.Message);
            await WriteAsync(context, exception.Code, exception.Message, exception.Details);
        }
        catch (BadHttpRequestException exception)
        {
            _logger.LogWarning(exception, "请求格式错误：{Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, ApiErrorCode.BadRequest, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "未处理异常：{Method} {Path} traceId={TraceId}",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);
            var message = _exposeDetails
                ? $"{exception.GetType().Name}: {exception.Message}"
                : "服务器内部错误，请联系管理员并提供 traceId。";
            await WriteAsync(context, ApiErrorCode.Unknown, message);
        }
    }

    private static async Task WriteAsync(
        HttpContext context,
        ApiErrorCode code,
        string? message,
        IReadOnlyDictionary<string, string>? details = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = ApiErrorHttpStatus.Map(code);
        context.Response.ContentType = "application/json; charset=utf-8";

        object? data = details is { Count: > 0 } ? details : null;
        var payload = ApiResult.Fail<object?>(code, message, data, context.TraceIdentifier);
        await context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
    }
}

/// <summary>中间件注册扩展。</summary>
public static class GlobalExceptionMiddlewareExtensions
{
    /// <summary>启用全局异常翻译。应尽早注册（在认证之前），确保认证失败之外的异常也能被统一包装。</summary>
    public static IApplicationBuilder UsePlatformExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<GlobalExceptionMiddleware>();
}
