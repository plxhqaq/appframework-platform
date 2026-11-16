using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Auth;
using Platform.Api.Diagnostics;
using Platform.Api.Endpoints;
using Platform.Api.Modules;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Contracts.Documents;
using Platform.Contracts.Hubs;
using Platform.Infrastructure.DependencyInjection;
using Platform.Infrastructure.Deployment;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.DependencyInjection;
using Platform.Kernel.Hubs;
using Platform.Kernel.Http;
using Platform.Kernel.Modules;
using Platform.Kernel.Metrics;
using Platform.Kernel.Middleware;
using Platform.Modules.Identity.Services;
using Platform.Modules.Devices.Hubs;
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

    // ── 实时推送 ──────────────────────────────────────────────────────
    // 推送只作提示（授权/版本/扫码审批），客户端收到后必须回拉接口校验。
    builder.Services.AddSignalR();

    // ── 宿主诊断 ──────────────────────────────────────────────────────
    // 后台采样进程 CPU/内存，仪表盘只读快照，不在请求线程里等采样窗口。
    builder.Services.AddSingleton<SystemMetricsSampler>();
    builder.Services.AddHostedService(static sp => sp.GetRequiredService<SystemMetricsSampler>());

    // ── 模块（集中清单是唯一装配点）──────────────────────────────────
    builder.Services.AddPlatformModules(PlatformModuleCatalog.Modules, builder.Configuration);

    // ── 文档与限流 ────────────────────────────────────────────────────
    // FormOptions 默认 128MB，会把 200MB 文档挡在业务校验之前；与文档上限 + multipart 开销对齐。
    builder.Services.Configure<FormOptions>(options =>
    {
        var maxFileSize = builder.Configuration.GetValue<long?>("Documents:MaxFileSizeBytes")
                          ?? DocumentConventions.MaxFileSizeBytes;
        options.MultipartBodyLengthLimit = maxFileSize + RequestLimits.MultipartOverheadBytes;
    });

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
        options.AddPolicy(PlatformRateLimitPolicies.SingleLoginRedeem, httpContext =>
        {
            // 6 位密钥空间只有 100 万，必须把单 IP 的兑换速度压到无法枚举。
            // 请求期读配置：集成测试通过配置放大额度，生产默认 10/分钟。
            var permitLimit = httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("RateLimits:SingleLoginRedeemPerMinute", 10);
            var partitionKey = $"ip:{httpContext.Connection.RemoteIpAddress}";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
        options.AddPolicy(PlatformRateLimitPolicies.QrSession, httpContext =>
        {
            // 创建与轮询共用额度；客户端轮询通常 2–5 秒一次，60/分钟足够。
            var permitLimit = httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("RateLimits:QrSessionPerMinute", 60);
            var partitionKey = $"ip:{httpContext.Connection.RemoteIpAddress}";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
        options.AddPolicy(PlatformRateLimitPolicies.VersionsUploadTicket, httpContext =>
        {
            // 已认证的管理端入口：按用户 + 应用分区，避免一个脚本刷爆整个应用的配额。
            var permitLimit = httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("RateLimits:VersionsUploadTicketPerMinute", 60);
            var partitionKey = VersionRateLimitPartitioning.For(httpContext, "upload-ticket");
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
        options.AddPolicy(PlatformRateLimitPolicies.VersionsPackageFinalize, httpContext =>
        {
            var permitLimit = httpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("RateLimits:VersionsPackageFinalizePerMinute", 30);
            var partitionKey = VersionRateLimitPartitioning.For(httpContext, "package-finalize");
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
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

    // 显式迁移入口：`dotnet Platform.Api.dll --migrate`（容器里同一入口）。
    // 生产多副本不能各自抢迁移，所以 AutoMigrate 只留给开发，这里提供一次性跑完即退的替代路径。
    if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
    {
        await using var migrateScope = app.Services.CreateAsyncScope();
        var migrateDb = migrateScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await migrateDb.Database.MigrateAsync();
        Log.Information("数据库迁移已完成，退出。");
        return;
    }

    // 现场找回管理员口令：`dotnet Platform.Api.dll --reset-admin-password`（容器里由
    // deploy/deploy.sh reset-admin-password 调用）。口令走 Seed__AdminPassword，不走命令行参数
    // ——argv 会进 ps 输出与容器 inspect，等于把引导口令贴给整台机器看。
    if (args.Contains("--reset-admin-password", StringComparer.OrdinalIgnoreCase))
    {
        await using var resetScope = app.Services.CreateAsyncScope();
        await resetScope.ServiceProvider.GetRequiredService<IdentitySeeder>().ResetAdminPasswordAsync();
        Log.Information("管理员引导口令已按 Seed:AdminPassword 写好，登录名 admin；重跑部署信息：deploy/deploy.sh info。");
        return;
    }

    // 开发期可选自动迁移；生产由部署脚本显式执行，避免多副本同时迁移。
    if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.MigrateAsync();
    }

    // ── 单实例守卫 ────────────────────────────────────────────────────
    // 投递、回执与推送都依赖进程内状态，当前版本只能单副本。用 PostgreSQL 会话级 advisory lock
    // 把「第二个副本」挡在启动阶段，而不是让它跑起来后再重复外呼。连接需活到进程退出。
    await using var instanceScope = app.Services.CreateAsyncScope();
    var instanceGuard = instanceScope.ServiceProvider.GetRequiredService<SingleInstanceGuard>();
    if (app.Configuration.GetValue("Deployment:RequireSingleInstance", true))
    {
        if (await instanceGuard.AcquireAsync())
        {
            Log.Information(
                "已获得单实例锁（PostgreSQL advisory lock {LockKey}），当前进程为唯一活动副本。",
                SingleInstanceGuard.AdvisoryLockKey);
        }
    }
    else
    {
        // 熔断开关：只有已经实现投递行级认领 + SignalR 背板后才允许关闭。
        Log.Warning(
            "Deployment:RequireSingleInstance=false，跳过单实例校验。"
            + "多副本必须自行保证 Webhook 投递不重复、备份回执与 SignalR 推送可跨副本路由。");
    }

    // ── 管道 ──────────────────────────────────────────────────────────
    // 反向代理（nginx）后必须信任转发头，否则限流分区与审计 IP 全是 127.0.0.1
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    });

    app.UsePlatformExceptionHandling();
    // 显式 UseRouting：否则路由信息在 UseAuthentication 时才注入，指标中间件拿不到端点，
    // 所有请求都会被记成 unmatched，路由级告警全部失效。
    app.UseRouting();
    app.UsePlatformMetrics();
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
    // 令牌自包含：应用停用后，旧访问令牌要靠请求路径上的短缓存校验拦下来
    app.UsePlatformAppContextGuard();
    app.UseAuthorization();

    // ── 端点 ──────────────────────────────────────────────────────────
    app.MapPlatformEndpoints();
    app.MapAuditEndpoints();
    app.MapPlatformMetrics();
    app.MapPlatformModules();
    app.MapHub<PlatformHub>(PlatformHubPaths.Root);
    app.MapHub<DeviceHub>(DeviceHubPaths.Root);

    // OpenAPI 3.1 导出 + Scalar 在线调试
    app.MapOpenApi();
    app.MapScalarApiReference();

    // 开发令牌端点：仅开发环境 + 显式开关
    if (app.Environment.IsDevelopment()
        && app.Configuration.GetValue<bool>($"{JwtOptions.SectionName}:{nameof(JwtOptions.AllowDevTokenEndpoint)}"))
    {
        app.MapDevTokenEndpoints();
    }

    try
    {
        await app.RunAsync();
    }
    finally
    {
        await instanceGuard.ReleaseAsync();
    }
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

/// <summary>版本入口限流的分区键：同一用户在同一应用内共享额度。</summary>
internal static class VersionRateLimitPartitioning
{
    public static string For(HttpContext context, string scope)
    {
        var user = context.User.FindFirst("sub")?.Value
                   ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? context.User.Identity?.Name
                   ?? "anonymous";
        var app = context.Request.Headers[PlatformHeaders.AppKey].ToString();
        return $"{scope}:user:{user}|app:{app}";
    }
}
