using Microsoft.AspNetCore.Http;
using Platform.Contracts;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;

namespace Platform.Kernel.Http;

/// <summary>
/// 创建类端点的幂等执行器：读取 <c>Idempotency-Key</c>，同键在 TTL 内只执行一次并回放首次响应。
/// </summary>
/// <remarks>
/// 约定：
/// <list type="bullet">
/// <item>未携带请求头时按普通请求执行（幂等是客户端的主动权，不强制）；</item>
/// <item>占位失败但已有完成结果时回放结果；仍无结果说明并发请求正在处理，返回 409；</item>
/// <item>执行业务失败或抛异常时释放占位，允许客户端用同一键重试。</item>
/// </list>
/// </remarks>
public static class IdempotentExecution
{
    /// <summary>默认 TTL：覆盖客户端重试窗口即可，过长只会让幂等表膨胀。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    /// <summary>执行一次幂等受限的操作。</summary>
    /// <typeparam name="T">成功负载类型。</typeparam>
    /// <param name="context">当前请求。</param>
    /// <param name="store">幂等存储。</param>
    /// <param name="scope">作用域（建议 <c>{module}.{resource}.{action}</c>）。</param>
    /// <param name="execute">实际业务。</param>
    /// <param name="ttl">结果保留时长。</param>
    public static async Task<IResult> ExecuteAsync<T>(
        HttpContext context,
        IIdempotencyStore store,
        string scope,
        Func<CancellationToken, Task<ApiResult<T>>> execute,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(execute);

        var key = ReadKey(context);
        if (key is null)
        {
            return ToResult(context, await execute(context.RequestAborted));
        }

        var effectiveTtl = ttl ?? DefaultTtl;
        var replayed = await store.GetAsync<ApiResult<T>>(scope, key, context.RequestAborted);
        if (replayed is not null)
        {
            return ToResult(context, replayed with { TraceId = context.TraceIdentifier });
        }

        if (!await store.TryReserveAsync(scope, key, effectiveTtl, context.RequestAborted))
        {
            // 占位失败有两种可能：并发请求已完成（可回放），或者仍在处理中。
            var late = await store.GetAsync<ApiResult<T>>(scope, key, context.RequestAborted);
            if (late is not null)
            {
                return ToResult(context, late with { TraceId = context.TraceIdentifier });
            }

            throw new BusinessException(
                ApiErrorCode.Conflict,
                "相同 Idempotency-Key 的请求正在处理中，请稍后重试。");
        }

        try
        {
            var result = await execute(context.RequestAborted);
            if (result.IsSuccess)
            {
                await store.SaveAsync(scope, key, result, effectiveTtl, context.RequestAborted);
            }
            else
            {
                await store.ReleaseAsync(scope, key, context.RequestAborted);
            }

            return ToResult(context, result);
        }
        catch
        {
            await store.ReleaseAsync(scope, key, CancellationToken.None);
            throw;
        }
    }

    private static string? ReadKey(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(PlatformHeaders.IdempotencyKey, out var values))
        {
            return null;
        }

        var key = values.ToString().Trim();
        if (key.Length == 0)
        {
            return null;
        }

        if (key.Length > 256)
        {
            throw BusinessException.Invalid("Idempotency-Key 长度不得超过 256 个字符。");
        }

        return key;
    }

    private static IResult ToResult<T>(HttpContext context, ApiResult<T> result)
    {
        var payload = result.TraceId is null ? result with { TraceId = context.TraceIdentifier } : result;
        var status = payload.IsSuccess
            ? StatusCodes.Status200OK
            : ApiErrorHttpStatus.Map((ApiErrorCode)payload.Code);
        return Results.Json(payload, statusCode: status);
    }
}
