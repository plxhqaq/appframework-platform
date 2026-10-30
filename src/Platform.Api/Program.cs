using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Auth;
using Platform.Api.Endpoints;
using Platform.Api.Modules;
using Platform.Contracts;
using Platform.Infrastructure.DependencyInjection;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.DependencyInjection;
using Platform.Kernel.Modules;
using Platform.Kernel.Middleware;
using Platform.Workers.DependencyInjection;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

// ═══════════════════════════════════════════════════════════════════════════
// AppFramework 通用服务提供平台 — 宿主装配
//
// 装配顺序即约束顺序：
//   内核（契约/主体/权限） → 基础设施（库/存储） → 认证 → 模块清单 → 管道
// 任何一步失败都应在启动期炸掉，不留"跑起来才发现"的隐患。
// ═══════════════════════════════════════════════════════════════════════════

const string LogTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

// 静态日志只用于宿主构建完成前的致命错误；宿主运行期日志由下方 UseSerilog 接管。
// 不用 CreateBootstrapLogger：ReloadableLogger 每个进程只能 Freeze 一次，
// 多宿主场景（集成测试）会在第二次启动时抛 "logger is already frozen"。
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: LogTemplate, formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .MinimumLevel.Information()
        // 健康检查与 EF 命令日志降噪：这两类在正常运行时是噪音，会淹没真正的业务告警
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: LogTemplate, formatProvider: CultureInfo.InvariantCulture)
        .WriteTo.File(
            Path.Combine("logs", "platform-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            outputTemplate: LogTemplate,
            formatProvider: CultureInfo.InvariantCulture));

    // ── 内核与基础设施 ────────────────────────────────────────────────
    builder.Services.AddPlatformKernel();
    builder.Services.AddPlatformInfrastructure(builder.Configuration);
    builder.Services.AddPlatformWorkers(builder.Configuration);

    // ── 认证授权 ──────────────────────────────────────────────────────
    builder.Services.AddPlatformAuthentication(builder.Configuration);
    builder.Services.AddSingleton<JwtTokenIssuer>();
    builder.Services.AddSingleton<IAccessTokenIssuer>(static sp => sp.GetRequiredService<JwtTokenIssuer>());

    // ── 模块（集中清单是唯一装配点）──────────────────────────────────
    builder.Services.AddPlatformModules(PlatformModuleCatalog.Modules, builder.Configuration);

    // ── 文档与限流 ────────────────────────────────────────────────────
    builder.Services.AddOpenApi();
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(PlatformRateLimitPolicies.M2MToken, httpContext =>
        {
            // 匿名入口：按应用键分区（同一应用的多个副本共享额度），无应用键时退回按 IP。
            var appKey = httpContext.Request.Headers[PlatformHeaders.AppKey].ToString();
            var partitionKey = string.IsNullOrWhiteSpace(appKey)
                ? $"ip:{httpContext.Connection.RemoteIpAddress}"
                : $"app:{appKey}";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
        options.AddPolicy(PlatformRateLimitPolicies.IdentityLogin, httpContext =>
        {
            // IP 级粗粒度限流；账号/分布式撞库由 login_throttles 双维度节流负责。
            var partitionKey = $"ip:{httpContext.Connection.RemoteIpAddress}";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
            await context.HttpContext.Response.WriteAsJsonAsync(
                ApiResult.Fail(
                    ApiErrorCode.RateLimited,
                    "请求过于频繁，请稍后重试。",
                    context.HttpContext.TraceIdentifier),
                cancellationToken);
        };
    });

    var app = builder.Build();

    // 开发期可选自动迁移；生产由部署脚本显式执行，避免多副本同时迁移。
    if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.MigrateAsync();
    }

    // ── 管道 ──────────────────────────────────────────────────────────
    // 反向代理（nginx）后必须信任转发头，否则限流分区与审计 IP 全是 127.0.0.1
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    });

    app.UsePlatformExceptionHandling();
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = static (httpContext, _, exception) =>
        {
            if (exception is not null) return LogEventLevel.Error;
            var path = httpContext.Request.Path;
            return path.StartsWithSegments("/healthz") || path.StartsWithSegments("/ready")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        };
    });

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    // ── 端点 ──────────────────────────────────────────────────────────
    app.MapPlatformEndpoints();
    app.MapPlatformModules();

    // OpenAPI 3.1 导出 + Scalar 在线调试
    app.MapOpenApi();
    app.MapScalarApiReference();

    // 开发令牌端点：仅开发环境 + 显式开关
    if (app.Environment.IsDevelopment()
        && app.Configuration.GetValue<bool>($"{JwtOptions.SectionName}:{nameof(JwtOptions.AllowDevTokenEndpoint)}"))
    {
        app.MapDevTokenEndpoints();
    }

    await app.RunAsync();
}
catch (Exception exception)
{
    Log.Fatal(exception, "平台启动失败。");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>宿主入口标记类型：供集成测试用 <c>WebApplicationFactory&lt;Program&gt;</c> 引用。</summary>
public partial class Program;
