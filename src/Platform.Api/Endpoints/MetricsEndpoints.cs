using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Metrics;

namespace Platform.Api.Endpoints;

/// <summary>
/// Prometheus 抓取端点：<c>GET /metrics</c>。
/// </summary>
/// <remarks>
/// 采集动作只发生在这里：模块的 <see cref="IPlatformMetricSource"/> 不会被后台循环调用，
/// 因此没有指标采集的额外常驻开销，代价是抓取频率要和采集端一致（默认 15s 一次可接受）。
/// </remarks>
public static class MetricsEndpoints
{
    private const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>指标抓取配置节。</summary>
    public const string SectionName = "Metrics";

    /// <summary>挂载 <c>/metrics</c>；<c>Metrics:Enabled=false</c> 时完全不暴露。</summary>
    public static IEndpointRouteBuilder MapPlatformMetrics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue($"{SectionName}:Enabled", true))
        {
            return endpoints;
        }

        var scrapeToken = configuration[$"{SectionName}:ScrapeToken"];

        endpoints.MapGet(
                "/metrics",
                async (HttpContext context, PlatformMetrics metrics, CancellationToken cancellationToken) =>
                {
                    if (!IsAuthorized(context, scrapeToken))
                    {
                        // 不用 ApiResult：采集端只认 Prometheus 文本，JSON 会被判为抓取失败。
                        return Results.Text(
                            "metrics scrape token missing or invalid",
                            ContentType,
                            statusCode: StatusCodes.Status401Unauthorized);
                    }

                    foreach (var source in context.RequestServices.GetServices<IPlatformMetricSource>())
                    {
                        try
                        {
                            await source.CollectAsync(metrics, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // 单个模块查库失败（库抖动、表还没迁移）不能让整次抓取失效：
                            // 采集端宁可看到进程指标 + 一个错误计数，也不要 500。
                            metrics.Increment(
                                PlatformMetricNames.MetricSourceErrors,
                                PlatformMetricNames.MetricSourceErrorsHelp,
                                new Dictionary<string, string> { ["source"] = source.Name });
                        }
                    }

                    return Results.Text(metrics.Render(), ContentType);
                })
            .WithName("platform.metrics")
            .WithTags("platform")
            .ExcludeFromDescription()
            .AllowAnonymous();

        return endpoints;
    }

    private static bool IsAuthorized(HttpContext context, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            // 未配置令牌时不鉴权：开发环境直连方便，生产由 nginx 限制来源（见可观测文档）。
            return true;
        }

        var provided = context.Request.Headers["X-Metrics-Token"].ToString();
        if (string.IsNullOrEmpty(provided))
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            provided = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization[7..]
                : string.Empty;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var actualBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
