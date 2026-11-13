using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Documents;
using Platform.Contracts.Licensing;
using Platform.Contracts.Versions;

namespace AppFramework.Platform.Client;

public sealed partial class PlatformClient
{
    private const string VersionCheckPath = "/api/v1/client/versions/check";
    private const string VersionManifestPath = "/api/v1/client/versions";
    private const string VersionDownloadPath = "/api/v1/client/versions/download";
    private const string VersionPackagesPath = "/api/v1/client/versions/packages";
    private const string UpdaterPath = "/api/v1/client/versions/updater";
    private const string UpdatersPath = "/api/v1/client/versions/updaters";
    private const string LicensingStatusPath = "/api/v1/client/licensing/status";
    private const string LicensingOfflineVerifyPath = "/api/v1/client/licensing/offline/verify";
    private const string LicensingKeysPath = "/api/v1/client/licensing/keys";
    private const string DocumentsManifestPath = "/api/v1/client/documents/manifest";
    private const string DocumentsPath = "/api/v1/client/documents";
    private const string BackupsPath = "/api/v1/client/backups";
    private const string BackupsDownloadSuffix = "/download";

    /// <summary>
    /// 检查更新。无新版本时响应里的 <c>Release</c> 仍是当前最新版清单，客户端可据此做完整性修复。
    /// <paramref name="updaterVersion"/> 是本机更新器版本，服务端据此给出 <c>HasUpdaterUpdate</c>。
    /// <paramref name="channelKey"/> 与 <paramref name="audienceTags"/> 至少给一个：给渠道键时以渠道键为准，
    /// 只给标签时由服务端按渠道受众匹配（响应回填解析出的渠道键）。
    /// </summary>
    public Task<CheckUpdateResponse> CheckUpdateAsync(
        string? channelKey = null,
        string? currentVersion = null,
        string? runtimeIdentifier = null,
        string? updaterVersion = null,
        bool allowDowngrade = false,
        IReadOnlyList<string>? audienceTags = null,
        CancellationToken cancellationToken = default) =>
        AuthorizedCallAsync<CheckUpdateResponse>(
            HttpMethod.Post,
            VersionCheckPath,
            new CheckUpdateRequest(
                channelKey,
                currentVersion,
                runtimeIdentifier ?? Options.RuntimeIdentifier,
                updaterVersion,
                allowDowngrade,
                audienceTags),
            cancellationToken);

    /// <summary>读取某渠道某版本的清单（文件路径 + 哈希 + 长度，按路径排序）。</summary>
    public Task<ReleaseManifestDto> GetReleaseManifestAsync(
        string channelKey,
        string version,
        string? runtimeIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        var rid = runtimeIdentifier ?? Options.RuntimeIdentifier;
        var path = $"{VersionManifestPath}/{Uri.EscapeDataString(channelKey)}/{Uri.EscapeDataString(version)}/manifest";
        if (!string.IsNullOrEmpty(rid))
        {
            path += $"?runtimeIdentifier={Uri.EscapeDataString(rid)}";
        }

        return AuthorizedCallAsync<ReleaseManifestDto>(HttpMethod.Get, path, body: null, cancellationToken);
    }

    /// <summary>
    /// 按内容哈希下载版本文件。服务端 ETag 就是哈希本身，因此 <paramref name="ifNoneMatch"/>
    /// 只在「本地已有同一哈希、想确认服务端仍然一致」时传入 —— 传了它而内容未变会返回 <c>false</c> 而不是重新落盘。
    /// </summary>
    /// <returns>是否真的下载并落盘（<c>false</c> = 304 未变更）。</returns>
    public Task<bool> DownloadVersionFileAsync(
        string sha256,
        string filePath,
        string? ifNoneMatch = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(
            $"{VersionDownloadPath}/{Uri.EscapeDataString(sha256)}",
            filePath,
            expectedSha256: sha256,
            ifNoneMatch,
            cancellationToken);

    /// <summary>下载全量/增量安装包（保留服务端文件名；哈希不符时拒绝落盘）。</summary>
    public Task<bool> DownloadReleasePackageAsync(
        Guid packageId,
        string filePath,
        string? expectedSha256 = null,
        string? ifNoneMatch = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(
            $"{VersionPackagesPath}/{packageId:D}/download",
            filePath,
            expectedSha256,
            ifNoneMatch,
            cancellationToken);

    /// <summary>当前运行时下最新的已发布更新器；没有时抛 <see cref="ApiErrorCode.VersionsUpdaterNotFound"/>。</summary>
    public Task<UpdaterInfoDto> GetLatestUpdaterAsync(
        string? runtimeIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        var rid = runtimeIdentifier ?? Options.RuntimeIdentifier;
        var path = UpdaterPath;
        if (!string.IsNullOrEmpty(rid))
        {
            path += $"?runtimeIdentifier={Uri.EscapeDataString(rid)}";
        }

        return AuthorizedCallAsync<UpdaterInfoDto>(HttpMethod.Get, path, body: null, cancellationToken);
    }

    /// <summary>下载更新器分发包（保留服务端文件名；哈希不符时拒绝落盘）。</summary>
    public Task<bool> DownloadUpdaterAsync(
        Guid updaterId,
        string filePath,
        string? expectedSha256 = null,
        string? ifNoneMatch = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(
            $"{UpdatersPath}/{updaterId:D}/download",
            filePath,
            expectedSha256,
            ifNoneMatch,
            cancellationToken);

    /// <summary>下载文档当前修订（服务端会再校验可见性，internal 文档一律拒绝）。</summary>
    public Task<bool> DownloadDocumentAsync(
        Guid documentId,
        string filePath,
        string? expectedSha256 = null,
        string? ifNoneMatch = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(
            $"{DocumentsPath}/{documentId:D}{BackupsDownloadSuffix}",
            filePath,
            expectedSha256,
            ifNoneMatch,
            cancellationToken);

    /// <summary>客户端文档清单：只含 <c>client</c> 可见且所属集合启用的文档。</summary>
    public Task<ClientDocumentManifest> GetDocumentManifestAsync(CancellationToken cancellationToken = default) =>
        AuthorizedCallAsync<ClientDocumentManifest>(HttpMethod.Get, DocumentsManifestPath, body: null, cancellationToken);

    /// <summary>在线授权状态（含签名快照与受信任公钥）。</summary>
    public Task<LicenseStatusResponse> GetLicenseStatusAsync(
        string productKey,
        string machineCode,
        CancellationToken cancellationToken = default) =>
        AuthorizedCallAsync<LicenseStatusResponse>(
            HttpMethod.Post, LicensingStatusPath, new LicenseStatusRequest(productKey, machineCode), cancellationToken);

    /// <summary>请服务端校验离线授权码（客户端离线时改用 <see cref="LicenseVerifier"/> 本地验签）。</summary>
    public Task<OfflineLicenseVerificationDto> VerifyOfflineLicenseCodeAsync(
        string code,
        string machineCode,
        string productKey,
        CancellationToken cancellationToken = default) =>
        AuthorizedCallAsync<OfflineLicenseVerificationDto>(
            HttpMethod.Post,
            LicensingOfflineVerifyPath,
            new VerifyOfflineLicenseCodeRequest(code, machineCode, productKey),
            cancellationToken);

    /// <summary>受信任签名公钥目录（匿名端点，安装包可内置）。</summary>
    public async Task<IReadOnlyList<LicenseKeyDescriptorDto>> GetTrustedSigningKeysAsync(CancellationToken cancellationToken = default) =>
        await CallAsync<List<LicenseKeyDescriptorDto>>(HttpMethod.Get, LicensingKeysPath, body: null, useToken: false, cancellationToken);

    /// <summary>读取下发备份的元数据。</summary>
    public Task<BackupDto> GetBackupAsync(Guid backupId, CancellationToken cancellationToken = default) =>
        AuthorizedCallAsync<BackupDto>(HttpMethod.Get, $"{BackupsPath}/{backupId:D}", body: null, cancellationToken);

    /// <summary>读取下发备份的内容（≤64 KB 内联，超过则流式取回；都是 JSON 文本）。</summary>
    public async Task<string> ReadBackupContentAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"{BackupsPath}/{backupId:D}{BackupsDownloadSuffix}", body: null, useToken: true, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowFailureAsync(response, cancellationToken);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<bool> DownloadAsync(
        string path,
        string filePath,
        string? expectedSha256,
        string? ifNoneMatch,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (AccessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (ifNoneMatch is { Length: > 0 } etag)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{etag}\""));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowFailureAsync(response, cancellationToken);
        }

        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // 半截文件比下载失败更危险：先写临时文件，哈希校验通过再原子改名。
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string actualSha256;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                actualSha256 = await CopyAndHashAsync(source, target, cancellationToken);
            }

            if (expectedSha256 is { Length: > 0 }
                && !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PlatformClientException(
                    ApiErrorCode.VersionsPackageHashMismatch,
                    $"下载内容哈希不符：期望 {expectedSha256}，实际 {actualSha256}。",
                    (int)response.StatusCode,
                    traceId: null);
            }

            File.Move(tempPath, fullPath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<string> CopyAndHashAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    /// <summary>下载类端点不是 JSON 信封（错误时才可能是），这里把两种情况都归一成异常。</summary>
    private static async Task ThrowFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var envelope = TryReadEnvelope<object?>(text);
        throw new PlatformClientException(
            envelope is not null ? (ApiErrorCode)envelope.Code : CodeForStatus(response.StatusCode),
            envelope?.Message ?? $"服务端返回 HTTP {(int)response.StatusCode}。",
            (int)response.StatusCode,
            envelope?.TraceId);
    }
}
