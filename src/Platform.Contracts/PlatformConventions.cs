namespace Platform.Contracts;

/// <summary>模块键。与 <c>IPlatformModule.Key</c>、数据库 schema 后缀、错误码分段一一对应。</summary>
public static class PlatformModuleKeys
{
    public const string Apps = "apps";
    public const string Identity = "identity";
    public const string Versions = "versions";
    public const string Licensing = "licensing";
    public const string Backups = "backups";
    public const string Documents = "documents";
    public const string Webhooks = "webhooks";

    /// <summary>全部模块键（与 <c>PlatformModuleCatalog</c> 顺序一致）。</summary>
    public static IReadOnlyList<string> All { get; } =
        [Apps, Identity, Versions, Licensing, Backups, Documents, Webhooks];
}

/// <summary>路由前缀约定。URI 主版本 + 入口分离（管理端 / 客户端），可见性由服务端强制。</summary>
public static class PlatformRoutes
{
    /// <summary>API 前缀。</summary>
    public const string ApiPrefix = "/api/v1";

    /// <summary>管理端入口（React 后台使用）。</summary>
    public const string AdminPrefix = ApiPrefix + "/admin";

    /// <summary>客户端入口（桌面客户端使用；不得出现管理端才允许的数据）。</summary>
    public const string ClientPrefix = ApiPrefix + "/client";

    /// <summary>模块根路径，例如 <c>/api/v1/versions</c>。</summary>
    public static string Module(string moduleKey) => $"{ApiPrefix}/{moduleKey}";

    /// <summary>模块管理端子路径，例如 <c>/api/v1/admin/versions</c>。</summary>
    public static string AdminModule(string moduleKey) => $"{AdminPrefix}/{moduleKey}";

    /// <summary>模块客户端子路径，例如 <c>/api/v1/client/versions</c>。</summary>
    public static string ClientModule(string moduleKey) => $"{ClientPrefix}/{moduleKey}";
}

/// <summary>平台自定义请求/响应头约定。</summary>
public static class PlatformHeaders
{
    /// <summary>应用键（M2M 场景；也可用 API Key 认证方案携带）。</summary>
    public const string AppKey = "X-App-Key";

    /// <summary>API Key 明文（仅 M2M 换令牌端点使用，格式见 <c>AppConventions.ApiKeyPrefix</c>）。</summary>
    public const string ApiKey = "X-Api-Key";

    /// <summary>客户端版本，用于最低版本门禁（旧版打新接口时被拒）。</summary>
    public const string ClientVersion = "X-Client-Version";

    /// <summary>运行时标识（<c>win-x64</c> / <c>linux-x64</c> / <c>osx-arm64</c>）。</summary>
    public const string RuntimeIdentifier = "X-Runtime-Identifier";

    /// <summary>机器码（授权与设备绑定）。</summary>
    public const string MachineCode = "X-Machine-Code";

    /// <summary>幂等键（创建类 POST 必填）。</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>请求追踪号（未携带时由服务端生成并回写）。</summary>
    public const string RequestId = "X-Request-Id";
}

/// <summary>限流策略名。宿主注册策略，模块端点按名字引用，避免两边手写字符串漂移。</summary>
public static class PlatformRateLimitPolicies
{
    /// <summary>M2M 换令牌：匿名入口，按应用键（缺省时按 IP）限流。</summary>
    public const string M2MToken = "m2m-token";

    /// <summary>身份登录/刷新：匿名入口，按 IP 限流；账号维度节流由登录节流表负责。</summary>
    public const string IdentityLogin = "identity-login";

    /// <summary>单次密钥兑换：匿名入口，按 IP 严格限流（6 位密钥空间小，必须压住枚举速度）。</summary>
    public const string SingleLoginRedeem = "single-login-redeem";

    /// <summary>扫码会话创建与状态轮询：匿名入口，按 IP 限流。</summary>
    public const string QrSession = "qr-session";

    /// <summary>版本上传票据申请：管理端已认证入口，按用户 + 应用限流。</summary>
    public const string VersionsUploadTicket = "versions-upload-ticket";

    /// <summary>发布包/更新器登记：管理端已认证入口，按用户 + 应用限流。</summary>
    public const string VersionsPackageFinalize = "versions-package-finalize";
}

/// <summary>JWT 声明短名。**必须配合 <c>MapInboundClaims=false</c>**，否则会被改写成 <c>ClaimTypes.*</c> URI。</summary>
public static class PlatformClaimTypes
{
    /// <summary>用户主键。</summary>
    public const string Subject = "sub";

    /// <summary>用户名。</summary>
    public const string Name = "name";

    /// <summary>角色名（可多条）。</summary>
    public const string Role = "role";

    /// <summary>权限码（可多条；<c>*</c> 表示平台管理员通配）。</summary>
    public const string Permission = "perms";

    /// <summary>令牌可访问的应用键（可多条）。</summary>
    public const string App = "app";

    /// <summary>令牌类型：<c>user</c> / <c>service</c>（API Key 换取的 M2M 令牌）。</summary>
    public const string PrincipalKind = "pkind";

    /// <summary>临时令牌标记（单次密钥/扫码首登，需强制补全口令）。</summary>
    public const string Temporary = "is_temporary";

    /// <summary>令牌唯一 id。</summary>
    public const string JwtId = "jti";

    /// <summary>绑定的机器码（可空）。</summary>
    public const string MachineCode = "machine";
}

/// <summary><see cref="PlatformClaimTypes.PrincipalKind"/> 取值。</summary>
public static class PlatformPrincipalKinds
{
    public const string User = "user";
    public const string Service = "service";
}
