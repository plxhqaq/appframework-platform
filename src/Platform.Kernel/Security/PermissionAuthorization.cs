using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Permissions;

namespace Platform.Kernel.Security;

/// <summary>权限要求：持有 <see cref="Codes"/> 中任一（或全部）权限码才放行。</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>构造。</summary>
    /// <param name="codes">权限码集合。</param>
    /// <param name="requireAll">是否要求全部命中（默认任一命中）。</param>
    public PermissionRequirement(IEnumerable<string> codes, bool requireAll = false)
    {
        ArgumentNullException.ThrowIfNull(codes);
        Codes = [.. codes];
        if (Codes.Count == 0)
        {
            throw new ArgumentException("权限要求至少需要一个权限码。", nameof(codes));
        }
        RequireAll = requireAll;
    }

    /// <summary>权限码集合。</summary>
    public IReadOnlyList<string> Codes { get; }

    /// <summary>是否要求全部命中。</summary>
    public bool RequireAll { get; }
}

/// <summary>权限判定处理器。<c>perms</c> 声明含 <c>*</c> 视为管理员，直接放行。</summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        var granted = context.User
            .FindAll(PlatformClaimTypes.Permission)
            .Select(static c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (granted.Contains(PermissionCatalog.Wildcard))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var satisfied = requirement.RequireAll
            ? requirement.Codes.All(granted.Contains)
            : requirement.Codes.Any(granted.Contains);

        if (satisfied)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// 动态策略提供者：把 <c>RequirePermission("a","b")</c> 变成 <c>perm:any|a|b</c> 策略名，
/// 免去为每个权限码在 <c>AddAuthorization</c> 里手写一条策略。
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>策略名前缀。</summary>
    public const string PolicyPrefix = "perm:";

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    /// <summary>构造。</summary>
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    /// <summary>生成策略名。<c>perm:any|code1|code2</c> 或 <c>perm:all|code1|code2</c>。</summary>
    public static string PolicyName(IEnumerable<string> codes, bool requireAll = false)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var list = codes.Where(static c => !string.IsNullOrWhiteSpace(c)).ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("权限策略至少需要一个权限码。", nameof(codes));
        }
        return $"{PolicyPrefix}{(requireAll ? "all" : "any")}|{string.Join('|', list)}";
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var body = policyName[PolicyPrefix.Length..];
        var separator = body.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return Task.FromResult<AuthorizationPolicy?>(null);
        }

        var mode = body[..separator];
        var codes = body[(separator + 1)..]
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (codes.Length == 0)
        {
            return Task.FromResult<AuthorizationPolicy?>(null);
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(codes, string.Equals(mode, "all", StringComparison.Ordinal)))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>端点权限声明扩展。</summary>
public static class PermissionEndpointExtensions
{
    /// <summary>要求持有任一权限码。</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] codes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(PermissionPolicyProvider.PolicyName(codes));
    }

    /// <summary>要求同时持有全部权限码。</summary>
    public static TBuilder RequireAllPermissions<TBuilder>(this TBuilder builder, params string[] codes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(PermissionPolicyProvider.PolicyName(codes, requireAll: true));
    }
}
