using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Platform.Contracts;
using Platform.Contracts.Hubs;
using Platform.Contracts.Permissions;

namespace Platform.Kernel.Hubs;

/// <summary>
/// 管理台实时 Hub：浏览器专用，按令牌 perms 声明入权限域组。
/// **所有事件都只是提示**，前端收到后 invalidate 对应查询回拉接口，不得把推送当事实来源。
/// </summary>
[Authorize]
public sealed class AdminHub : Hub
{
    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var permissions = Context.User?.FindAll(PlatformClaimTypes.Permission)
            .Select(static claim => claim.Value)
            .ToArray() ?? [];

        foreach (var domain in ResolveDomains(permissions))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminHubGroups.Domain(domain));
        }

        await base.OnConnectedAsync();
    }

    /// <summary>轻量保活上行：浏览器周期调用，供服务端守卫校验令牌生命周期（过期/吊销即踢线）。</summary>
    public static Task Touch() => Task.CompletedTask;

    private static IReadOnlyList<string> ResolveDomains(string[] permissions)
    {
        if (permissions.Contains(PermissionCatalog.Wildcard))
        {
            return AdminHubDomains.All;
        }

        var domains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in permissions)
        {
            var domain = permission switch
            {
                _ when permission.StartsWith("installation.", StringComparison.Ordinal)
                    || permission.StartsWith("node.", StringComparison.Ordinal) => AdminHubDomains.Devices,
                _ when permission.StartsWith("identity.", StringComparison.Ordinal) => AdminHubDomains.Identity,
                _ when permission.StartsWith("versions.", StringComparison.Ordinal) => AdminHubDomains.Versions,
                _ when permission.StartsWith("license.", StringComparison.Ordinal) => AdminHubDomains.Licensing,
                _ when permission.StartsWith("apps.", StringComparison.Ordinal) => AdminHubDomains.Apps,
                _ => null,
            };
            if (domain is not null)
            {
                domains.Add(domain);
            }
        }

        return domains.ToArray();
    }
}