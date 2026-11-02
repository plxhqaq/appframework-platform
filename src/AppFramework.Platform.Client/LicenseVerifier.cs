using Platform.Contracts.Licensing;

namespace AppFramework.Platform.Client;

/// <summary>
/// 授权校验结论。
/// </summary>
/// <param name="IsValid">本机这台设备是否可以放行。</param>
/// <param name="Status">服务端给出的状态（<c>active</c>/<c>expired</c>/…）。</param>
/// <param name="Payload">验签通过的快照内容；验签失败时为 <c>null</c>。</param>
/// <param name="Reason">不放行的原因。</param>
public sealed record LicenseVerification(bool IsValid, string Status, LicenseSnapshotPayload? Payload, string? Reason);

/// <summary>
/// 离线授权码校验结论。
/// </summary>
/// <param name="IsValid">码可信、产品与机器码匹配且未过期（含宽限）。</param>
/// <param name="Content">解析并验签通过的码内容；不可信时为 <c>null</c>。</param>
/// <param name="Reason">不放行的原因。</param>
public sealed record OfflineCodeVerification(bool IsValid, OfflineLicenseCodeContent? Content, string? Reason);

/// <summary>
/// 把服务端授权响应变成「本机这台设备能不能放行」的结论。
/// 两条铁律：只信 ECDSA 验签通过的快照；到期判断用快照里的服务端时间，不用本机时钟（时钟可能被回拨）。
/// </summary>
public static class LicenseVerifier
{
    /// <summary>
    /// 校验授权快照，并核对快照里的应用键 / 产品键 / 机器码与期望一致。
    /// </summary>
    public static LicenseVerification Verify(
        LicenseStatusResponse status,
        string expectedAppKey,
        string expectedProductKey,
        string expectedMachineCode)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (string.Equals(status.Status, LicenseConventions.StatusUnmanaged, StringComparison.Ordinal))
        {
            return Reject(status, "服务端未托管签名密钥，授权结论不可采信。");
        }

        if (status.Snapshot is not { Length: > 0 } token)
        {
            return Reject(status, "响应没有签名快照（未授权或未被签发）。");
        }

        var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in status.TrustedKeys)
        {
            trustedKeys[key.KeyId] = key.PublicKey;
        }

        if (!LicenseSnapshotCodec.TryVerify(token, trustedKeys, out var payload, out var verifyError))
        {
            return Reject(status, verifyError ?? "快照验签失败。");
        }

        if (!string.Equals(payload.AppKey, expectedAppKey, StringComparison.Ordinal))
        {
            return Reject(status, $"快照绑定的应用键是 {payload.AppKey}，与期望的 {expectedAppKey} 不符。", payload);
        }

        if (!string.Equals(payload.ProductKey, expectedProductKey, StringComparison.Ordinal))
        {
            return Reject(status, $"快照绑定的产品键是 {payload.ProductKey}，与期望的 {expectedProductKey} 不符。", payload);
        }

        if (!MachinesMatch(payload.MachineCode, expectedMachineCode))
        {
            return Reject(status, "快照绑定的机器码与本机不符。", payload);
        }

        if (string.Equals(payload.Status, LicenseConventions.StatusRevoked, StringComparison.Ordinal))
        {
            return Reject(status, "授权已吊销。", payload);
        }

        if (payload.NotBefore > payload.ServerTime)
        {
            return Reject(status, "授权尚未生效。", payload);
        }

        if (payload.NotAfter.AddDays(payload.GraceDays) < payload.ServerTime)
        {
            return Reject(status, $"授权已过期（含宽限 {payload.GraceDays} 天）。", payload);
        }

        return new LicenseVerification(true, status.Status, payload, null);
    }

    /// <summary>
    /// 离线授权码本地校验：产品键 + 机器码绑定，跨机复制必然失败。
    /// </summary>
    /// <param name="code">服务端签发的原始码（带分隔符的形态也可以）。</param>
    /// <param name="machineCode">本机机器码。</param>
    /// <param name="productKey">产品键。</param>
    /// <param name="trustedPublicKeys">受信任公钥（SPKI Base64）集合，可内置于安装包。</param>
    public static OfflineCodeVerification VerifyOfflineCode(
        string code,
        string machineCode,
        string productKey,
        IReadOnlyCollection<string> trustedPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);

        if (!OfflineLicenseCodec.TryParse(code, trustedPublicKeys, out var content, out var error))
        {
            return new OfflineCodeVerification(false, null, error ?? "离线码校验失败。");
        }

        if (!string.Equals(content.ProductKey, productKey, StringComparison.Ordinal))
        {
            return new OfflineCodeVerification(false, content, $"离线码属于产品 {content.ProductKey}。");
        }

        if (!MachinesMatch(content.MachineCode, machineCode))
        {
            return new OfflineCodeVerification(false, content, "离线码绑定的机器码与本机不符。");
        }

        // 离线码没有服务端时间：只能以本机时钟判断到期，宽限期仍按快照里的天数计算。
        var now = DateTimeOffset.UtcNow;
        if (content.NotAfter.AddDays(content.GraceDays) < now)
        {
            return new OfflineCodeVerification(false, content, "离线码已过期（含宽限期）。");
        }

        return new OfflineCodeVerification(true, content, null);
    }

    private static bool MachinesMatch(string? actual, string? expected)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return true;
        }

        // 手抄的 O/I/L 会被规范化成 0/1/1，否则同一台机器会因为大小写/误抄被判定为不匹配
        return LicenseConventions.TryNormalizeMachineCode(actual, out var left)
            && LicenseConventions.TryNormalizeMachineCode(expected, out var right)
            && string.Equals(left, right, StringComparison.Ordinal);
    }

    private static LicenseVerification Reject(LicenseStatusResponse status, string reason, LicenseSnapshotPayload? payload = null) =>
        new(false, status.Status, payload, reason);
}
