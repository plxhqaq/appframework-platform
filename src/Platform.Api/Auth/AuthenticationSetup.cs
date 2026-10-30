using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Platform.Contracts;

namespace Platform.Api.Auth;

/// <summary>认证装配。</summary>
public static class AuthenticationSetup
{
    /// <summary>注册 JWT Bearer 认证。</summary>
    /// <remarks>
    /// 三个关键点：
    /// <list type="number">
    /// <item><c>MapInboundClaims = false</c>：否则 <c>sub</c>/<c>perms</c> 被改写成 <c>ClaimTypes.*</c> URI，主体读取全空（现网踩过）；</item>
    /// <item>401/403 也返回统一 <see cref="ApiResult{T}"/>，客户端只需一处解析；</item>
    /// <item>签名密钥 <b>至少 32 字符</b>，缺失即启动失败（不允许悄悄用弱密钥）。</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(options.SigningKey) || options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "缺少 Jwt:SigningKey 或长度不足 32 字符。请通过环境变量 Jwt__SigningKey 注入强随机密钥。");
        }

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(bearer =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = PlatformClaimTypes.Name,
                    RoleClaimType = PlatformClaimTypes.Role,
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsJsonAsync(
                            ApiResult.Fail(
                                ApiErrorCode.Unauthorized,
                                "未认证或令牌无效/已过期。",
                                context.HttpContext.TraceIdentifier));
                    },
                    OnForbidden = async context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsJsonAsync(
                            ApiResult.Fail(
                                ApiErrorCode.Forbidden,
                                "当前主体缺少所需权限。",
                                context.HttpContext.TraceIdentifier));
                    },
                };
            });

        return services;
    }
}
