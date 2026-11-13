namespace Platform.Contracts.Identity;

/// <summary>单次密钥约定。密钥是 6 位数字、一次性消费，成功兑换后签发临时令牌。</summary>
public static class SingleLoginConventions
{
    /// <summary>密钥位数（十进制，含前导 0）。</summary>
    public const int KeyLength = 6;

    /// <summary>临时令牌使用的内置角色名。</summary>
    public const string EngineerRole = "engineer";

    /// <summary>临时令牌主体名（写入 <c>name</c> 声明）。</summary>
    public const string TemporaryUserName = "temp-user";

    /// <summary>临时令牌显示名。</summary>
    public const string TemporaryDisplayName = "临时用户";

    /// <summary>默认令牌有效期（分钟）。</summary>
    public const int DefaultDurationMinutes = 30;

    /// <summary>可设置的最长有效期（分钟，2 小时）：现场一次作业窗口通常按班次算，半小时不够用。</summary>
    public const int MaxDurationMinutes = 120;
}

/// <summary>扫码登录约定。二维码里放的是管理台审批页的 https 链接，手机相机直接扫开即可。</summary>
public static class QrLoginConventions
{
    /// <summary>
    /// 审批页路径前缀，后面接 <c>N</c> 格式的请求 id。
    /// 完整载荷 = 管理台公网基址 + 此路径 + 请求 id，例如 <c>https://ops.example.com:8444/qr-login/9f2c…</c>。
    /// </summary>
    public const string ApprovalPath = "/qr-login/";

    /// <summary>待审批会话的默认存活时间（分钟）。</summary>
    public const int ExpireMinutes = 10;

    /// <summary>审批通过后临时令牌的默认有效期（分钟）。</summary>
    public const int DefaultDurationMinutes = 30;

    /// <summary>审批时可指定的最长有效期（分钟，2 小时）。</summary>
    public const int MaxDurationMinutes = 120;
}

/// <summary>扫码会话状态。</summary>
public static class QrLoginStatuses
{
    /// <summary>等待审批。</summary>
    public const string Pending = "pending";

    /// <summary>已批准，可领取临时令牌。</summary>
    public const string Approved = "approved";

    /// <summary>已拒绝。</summary>
    public const string Rejected = "rejected";

    /// <summary>未审批即过期（读取时按时间计算）。</summary>
    public const string Expired = "expired";

    /// <summary>是否为合法状态值。</summary>
    public static bool IsValid(string? status) =>
        status is Pending or Approved or Rejected or Expired;
}

/// <summary>签发单次密钥。</summary>
/// <param name="DurationMinutes">密钥有效期（分钟）；空取默认值，超上限按上限钳制。</param>
/// <param name="DeviceFingerprint">绑定设备指纹；非空时只有同一指纹能兑换。</param>
public sealed record IssueSingleLoginKeyRequest(int? DurationMinutes = null, string? DeviceFingerprint = null);

/// <summary>签发结果。密钥明文只在此响应出现一次，服务端不提供回查。</summary>
public sealed record SingleLoginKeyDto(string Key, DateTimeOffset ExpiresAt, int DurationMinutes);

/// <summary>兑换单次密钥（客户端匿名入口）。</summary>
/// <param name="Key">6 位数字密钥。</param>
/// <param name="DeviceFingerprint">设备指纹；密钥绑定指纹时必须一致。</param>
/// <param name="MachineCode">机器码（写入令牌，供授权绑定）。</param>
public sealed record RedeemSingleLoginKeyRequest(
    string Key,
    string? DeviceFingerprint = null,
    string? MachineCode = null);

/// <summary>临时会话：只有访问令牌，没有刷新令牌——过期即失去访问，必须重新走单次登录。</summary>
public sealed record TemporarySessionResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    UserSessionDto User);

/// <summary>创建扫码会话（客户端匿名入口）。</summary>
/// <param name="DeviceFingerprint">设备指纹，展示给审批人。</param>
/// <param name="ClientName">客户端名称/机器名，展示给审批人。</param>
public sealed record CreateQrSessionRequest(string? DeviceFingerprint = null, string? ClientName = null);

/// <summary>扫码会话创建结果。</summary>
/// <param name="RequestId">会话 id（客户端轮询与审批的唯一标识）。</param>
/// <param name="ExpiresAt">待审批截止时间。</param>
/// <param name="Payload">二维码载荷文本：管理台审批页的完整 https 链接，末段即请求 id。</param>
public sealed record QrSessionCreatedDto(Guid RequestId, DateTimeOffset ExpiresAt, string Payload);

/// <summary>扫码会话状态（客户端轮询）。<see cref="Session"/> 仅在批准且令牌未过期时非空。</summary>
public sealed record QrSessionStatusDto(
    Guid RequestId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ApprovedAt,
    TemporarySessionResponse? Session);

/// <summary>管理端扫码会话概要。</summary>
public sealed record QrSessionSummaryDto(
    Guid RequestId,
    string Status,
    string? ClientName,
    string? DeviceFingerprint,
    string? CreatedByIp,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ApprovedAt,
    string? ApprovedByName);

/// <summary>批准扫码会话。</summary>
/// <param name="DurationMinutes">临时令牌有效期；空取默认值，超上限按上限钳制。</param>
public sealed record ApproveQrSessionRequest(int? DurationMinutes = null);
