using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Platform.Kernel.Metrics;

/// <summary>
/// HTTP 请求耗时采集。
/// </summary>
/// <remarks>
/// 路由标签只取模板（<c>/api/v1/admin/apps/{id:guid}</c>），绝不使用原始路径：
/// 路径里的 id 会让时间序列数量随数据线性增长，直接把采集端打爆。
/// 未匹配到端点的请求统一归入 <c>unmatched</c>，用于发现扫路径的流量。
/// </remarks>
public sealed class PlatformMetricsMiddleware(RequestDelegate next, PlatformMetrics metrics)
{
    /// <summary>执行请求并记录耗时。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // /metrics 自身不计数：否则每次抓取都会新增一条时间序列，且掩盖真实流量。
        if (context.Request.Path.StartsWithSegments("/metrics", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            metrics.ObserveHttp(
                context.Request.Method,
                RouteOf(context),
                context.Response.StatusCode,
                stopwatch.Elapsed.TotalSeconds);
        }
    }

    private static string RouteOf(HttpContext context)
    {
        if ((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is not { Length: > 0 } pattern)
        {
            return "unmatched";
        }

        // 组内用空模板注册的端点会带出结尾斜杠（/api/v1/admin/webhooks/），
        // 与整路径注册的同类端点写成两种标签值，仪表盘按路径聚合时会漏项。
        return pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;
    }
}

/// <summary>指标管道注册。</summary>
public static class PlatformMetricsApplicationBuilderExtensions
{
    /// <summary>挂载 HTTP 耗时采集，必须放在异常处理之后才能记到最终状态码。</summary>
    public static IApplicationBuilder UsePlatformMetrics(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<PlatformMetricsMiddleware>();
    }
}
