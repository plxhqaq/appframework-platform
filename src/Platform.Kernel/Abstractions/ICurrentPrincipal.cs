namespace Platform.Kernel.Abstractions;

/// <summary>
/// 当前请求主体。
/// </summary>
/// <remarks>
/// **隔离轴的唯一来源**：所有业务查询都通过 <see cref="AppKey"/> 过滤，
/// 而它来自令牌声明而不是客户端传参——避免越权枚举（沿用现网"可见性在服务端强制"的原则）。
/// </remarks>
public interface ICurrentPrincipal
{
    /// <summary>是否已认证。</summary>
    bool IsAuthenticated { get; }

    /// <summary>用户主键（服务令牌时为 API Key 的持有者标识）。</summary>
    string? UserId { get; }

    /// <summary>用户名。</summary>
    string? UserName { get; }

    /// <summary>角色名。</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>权限码集合（已从 <c>perms</c> 声明展开）。</summary>
    IReadOnlySet<string> Permissions { get; }

    /// <summary>令牌可访问的应用键集合。</summary>
    IReadOnlyList<string> AppKeys { get; }

    /// <summary>当前请求归属的应用键（单应用令牌时即该应用）。</summary>
    string? AppKey { get; }

    /// <summary>令牌绑定的安装实例标识（可空）。</summary>
    Guid? InstallGuid { get; }

    /// <summary>是否为临时令牌（单次密钥/扫码首登，需引导补全口令）。</summary>
    bool IsTemporary { get; }

    /// <summary>是否为服务到服务（M2M）主体。</summary>
    bool IsService { get; }

    /// <summary>是否拥有指定权限码（通配符 <c>*</c> 恒真）。</summary>
    bool HasPermission(string code);

    /// <summary>是否拥有任一权限码。</summary>
    bool HasAnyPermission(params string[] codes);
}
