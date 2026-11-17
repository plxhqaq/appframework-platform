using Platform.Contracts.Licensing;
using Platform.Modules.Licensing.Domain;

namespace Platform.Modules.Licensing.Services;

/// <summary>授权状态解析：吊销优先，其次看"到期 + 宽限"。</summary>
public static class LicenseStatusResolver
{
    /// <summary>是否「仅注销在线授权」：在线状态按未授权下发，已签发的离线码继续有效。</summary>
    public static bool IsOnlineOnlyRevoked(LicenseEntitlement entitlement)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        return entitlement.RevokedAt is not null
            && string.Equals(
                entitlement.RevokeScope,
                LicenseConventions.RevokeScopeOnline,
                StringComparison.Ordinal);
    }

    /// <summary>计算当前状态。</summary>
    public static string Resolve(LicenseEntitlement entitlement, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entitlement);

        if (entitlement.RevokedAt is not null)
        {
            return LicenseConventions.StatusRevoked;
        }

        return now <= entitlement.ExpiresAt
            ? LicenseConventions.StatusActive
            : LicenseConventions.StatusExpired;
    }
}
