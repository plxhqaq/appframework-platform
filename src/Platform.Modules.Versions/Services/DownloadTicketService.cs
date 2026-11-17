using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;

namespace Platform.Modules.Versions.Services;

/// <summary>
/// 管理台下载票据：浏览器不能带 Bearer 头，改为短期票据 URL，让下载交给浏览器自身
/// （流式落盘、暂停续传都由浏览器的下载管理器负责）。
/// </summary>
public interface IDownloadTicketService
{
    /// <summary>为某个发布包 / 更新器签发下载票据。</summary>
    DownloadTicketIssued Issue(string appKey, string kind, Guid id);

    /// <summary>校验并解析票据；无效或过期抛 401。</summary>
    DownloadTicket Read(string ticket);
}

/// <summary>票据种类。</summary>
public static class DownloadTicketKinds
{
    /// <summary>全量/增量安装包。</summary>
    public const string Package = "package";

    /// <summary>更新器分发包。</summary>
    public const string Updater = "updater";

    /// <summary>是否为合法票据种类。</summary>
    public static bool IsValid(string? kind) => kind is Package or Updater;
}

/// <summary>签发结果：URL 里用的票据串与到期时间。</summary>
public sealed record DownloadTicketIssued(string Ticket, DateTimeOffset ExpiresAt);

/// <summary>票据内容（只含定位信息，文件与权限在 GET 时按票据重新校验）。</summary>
public sealed record DownloadTicket(string AppKey, string Kind, Guid Id);

/// <inheritdoc />
public sealed class DownloadTicketService : IDownloadTicketService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(VersionConventions.DownloadTicketHours);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public DownloadTicketService(IDataProtectionProvider provider, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(clock);
        _protector = provider
            .CreateProtector("Platform.Versions.DownloadTicket.v1")
            .ToTimeLimitedDataProtector();
        _clock = clock;
    }

    /// <inheritdoc />
    public DownloadTicketIssued Issue(string appKey, string kind, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        if (!DownloadTicketKinds.IsValid(kind) || id == Guid.Empty)
        {
            throw new ArgumentException("下载票据参数非法。", nameof(kind));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new TicketPayload(appKey, kind, id));
        var ticket = WebEncoders.Base64UrlEncode(_protector.Protect(payload, Lifetime));
        return new DownloadTicketIssued(ticket, _clock.UtcNow.Add(Lifetime));
    }

    /// <inheritdoc />
    public DownloadTicket Read(string ticket)
    {
        TicketPayload? payload;
        try
        {
            var bytes = _protector.Unprotect(
                WebEncoders.Base64UrlDecode(ticket), out _);
            payload = JsonSerializer.Deserialize<TicketPayload>(bytes);
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            throw InvalidTicket();
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.AppKey)
            || !DownloadTicketKinds.IsValid(payload.Kind)
            || payload.Id == Guid.Empty)
        {
            throw InvalidTicket();
        }

        return new DownloadTicket(payload.AppKey, payload.Kind, payload.Id);
    }

    private static BusinessException InvalidTicket() =>
        new(ApiErrorCode.Unauthorized, "下载票据无效或已过期，请回到管理台重新点击下载。");

    private sealed record TicketPayload(string AppKey, string Kind, Guid Id);
}
