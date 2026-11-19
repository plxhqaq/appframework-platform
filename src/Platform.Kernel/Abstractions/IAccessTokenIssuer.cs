using Platform.Contracts;

namespace Platform.Kernel.Abstractions;

/// <summary>
/// 访问令牌签发契约。模块只描述"要签发什么"，具体签名算法与配置由宿主实现，
/// 这样模块不需要引用 JWT 库，也不会把签名密钥读进业务层。
/// </summary>
public interface IAccessTokenIssuer
{
    /// <summary>签发访问令牌。</summary>
    IssuedToken Issue(AccessTokenSpec spec);
}

/// <summary>签发请求。多值字段为空表示不写入对应声明。</summary>
public sealed record AccessTokenSpec
{
    /// <summary>主体 id（用户 id 或 API Key 标识）。</summary>
    public required string Subject { get; init; }

    /// <summary>主体名称（写入令牌，展示用）。</summary>
    public required string Name { get; init; }

    /// <summary>角色名。</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>权限码；<c>*</c> 表示平台管理员。</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>可访问的应用键。</summary>
    public IReadOnlyList<string> AppKeys { get; init; } = [];

    /// <summary>当前应用键（会排在 <see cref="AppKeys"/> 首位）。</summary>
    public string? AppKey { get; init; }

    /// <summary>是否临时令牌。</summary>
    public bool Temporary { get; init; }

    /// <summary>主体类型：<see cref="PlatformPrincipalKinds.User"/> / <see cref="PlatformPrincipalKinds.Service"/>。</summary>
    public string PrincipalKind { get; init; } = PlatformPrincipalKinds.User;

    /// <summary>绑定机器码。</summary>
    public string? MachineCode { get; init; }

    /// <summary>绑定的安装实例标识。</summary>
    public Guid? InstallGuid { get; init; }

    /// <summary>用户令牌代号（D7 epoch 守卫）：签发时从 <c>users.token_epoch</c> 取值；仅用户主体写入声明。</summary>
    public int? TokenEpoch { get; init; }

    /// <summary>有效期；为空取宿主配置。</summary>
    public TimeSpan? Lifetime { get; init; }
}

/// <summary>签发结果。</summary>
/// <param name="AccessToken">JWT 字符串。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);
