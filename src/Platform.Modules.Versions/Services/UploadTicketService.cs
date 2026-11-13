using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Options;

namespace Platform.Modules.Versions.Services;

/// <summary>上传票据：申请 → 一次性直传 → 内容寻址落盘。</summary>
public interface IUploadTicketService
{
    /// <summary>申请票据（只登记哈希与长度，不接收内容）。</summary>
    /// <param name="appKey">应用键。</param>
    /// <param name="request">声明的哈希与长度。</param>
    /// <param name="actor">申请主体。</param>
    /// <param name="uploadUrlPrefix">直传地址前缀（不含票据号）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<UploadTicketDto> CreateAsync(
        string appKey,
        CreateUploadTicketRequest request,
        string? actor,
        string uploadUrlPrefix,
        CancellationToken cancellationToken = default);

    /// <summary>读取仍可用的票据；不存在/过期/已消费分别映射 207/208/209。</summary>
    Task<UploadTicketInfo> GetPendingAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>消费票据并写入内容；票据并发只能成功一次。</summary>
    Task<UploadResultDto> UploadAsync(
        string appKey,
        Guid id,
        Stream content,
        long? declaredLength,
        CancellationToken cancellationToken = default);
}

/// <summary>只读票据信息（端点在读取请求体前用来限制长度）。</summary>
public sealed record UploadTicketInfo(Guid Id, string Sha256, long SizeBytes, DateTimeOffset ExpiresAt);

/// <summary>
/// 上传票据实现。消费语义与单次密钥相同：一条带条件的 UPDATE 决定归属，
/// 受影响行数为 1 的请求才能写内容；InMemory（测试用）退回进程内互斥 + 重查。
/// </summary>
public sealed partial class UploadTicketService : IUploadTicketService
{
    private const string TicketInvalidMessage = "上传票据无效。";
    private const string TicketExpiredMessage = "上传票据已过期。";
    private const string TicketConsumedMessage = "上传票据已被使用。";
    private const string HashMismatchMessage = "上传内容的 sha256 或长度与票据声明不符。";

    private static readonly SemaphoreSlim InMemoryGate = new(1, 1);

    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly VersionsOptions _options;

    /// <summary>构造。</summary>
    public UploadTicketService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<VersionsOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    /// <inheritdoc />
    public async Task<UploadTicketDto> CreateAsync(
        string appKey,
        CreateUploadTicketRequest request,
        string? actor,
        string uploadUrlPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadUrlPrefix);

        var sha256 = request.Sha256?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(sha256) || !Sha256Regex().IsMatch(sha256))
        {
            throw BusinessException.Invalid("sha256 必须是 64 位小写十六进制。");
        }

        // 单文件与增量包是两个上限；票据只声明整包字节数，按较大值放行，细粒度上限由发布清单校验。
        var maxSizeBytes = Math.Max(_options.MaxFileSizeBytes, _options.MaxPackageSizeBytes);
        if (request.SizeBytes <= 0 || request.SizeBytes > maxSizeBytes)
        {
            throw new BusinessException(
                ApiErrorCode.PayloadTooLarge,
                $"文件大小必须大于 0 且不超过 {maxSizeBytes} 字节。");
        }

        var now = _clock.UtcNow;
        var ticket = new VersionUploadTicket
        {
            AppKey = appKey,
            Sha256 = sha256,
            SizeBytes = request.SizeBytes,
            ExpiresAt = now.AddMinutes(Math.Max(1, _options.UploadTicketMinutes)),
            CreatedBy = actor,
        };

        _db.Set<VersionUploadTicket>().Add(ticket);
        await _db.SaveChangesAsync(cancellationToken);

        return new UploadTicketDto(ticket.Id, $"{uploadUrlPrefix}/{ticket.Id:D}", ticket.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<UploadTicketInfo> GetPendingAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var ticket = await _db.Set<VersionUploadTicket>()
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                     ?? throw new BusinessException(ApiErrorCode.VersionsUploadTicketInvalid, TicketInvalidMessage);

        ThrowIfUnusable(ticket, _clock.UtcNow);
        return new UploadTicketInfo(ticket.Id, ticket.Sha256, ticket.SizeBytes, ticket.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<UploadResultDto> UploadAsync(
        string appKey,
        Guid id,
        Stream content,
        long? declaredLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var ticket = await _db.Set<VersionUploadTicket>()
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                     ?? throw new BusinessException(ApiErrorCode.VersionsUploadTicketInvalid, TicketInvalidMessage);

        var now = _clock.UtcNow;
        ThrowIfUnusable(ticket, now);

        if (declaredLength is { } length && length != ticket.SizeBytes)
        {
            throw new BusinessException(ApiErrorCode.VersionsPackageHashMismatch, HashMismatchMessage);
        }

        await ConsumeAsync(appKey, id, ticket, now, cancellationToken);

        var blobPath = VersionBlobPaths.Blob(appKey, ticket.Sha256);
        if (await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            var existing = await _blobs.GetLengthAsync(blobPath, cancellationToken);
            if (existing == ticket.SizeBytes)
            {
                return new UploadResultDto(ticket.Sha256, ticket.SizeBytes, Reused: true);
            }

            // 同哈希不同长度说明 blob 已损坏；删掉重写，不能让坏内容继续被引用。
            await _blobs.DeleteAsync(blobPath, cancellationToken);
        }

        var written = await _blobs.WriteAsync(blobPath, content, cancellationToken);
        if (written.SizeBytes != ticket.SizeBytes
            || !string.Equals(written.Sha256, ticket.Sha256, StringComparison.Ordinal))
        {
            await _blobs.DeleteAsync(blobPath, cancellationToken);
            throw new BusinessException(ApiErrorCode.VersionsPackageHashMismatch, HashMismatchMessage);
        }

        return new UploadResultDto(ticket.Sha256, ticket.SizeBytes, Reused: false);
    }

    private async Task ConsumeAsync(
        string appKey,
        Guid id,
        VersionUploadTicket ticket,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            var affected = await _db.Set<VersionUploadTicket>()
                .Where(x => x.Id == id
                            && x.AppKey == appKey
                            && x.ConsumedAt == null
                            && x.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.ConsumedAt, now)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);

            if (affected == 1)
            {
                return;
            }

            // 消费失败：重新读取并按「不存在 / 已过期 / 已消费」分类抛出。
            await ThrowClassifiedAsync(appKey, id, now, cancellationToken);
            return;
        }

        await InMemoryGate.WaitAsync(cancellationToken);
        try
        {
            var tracked = await _db.Set<VersionUploadTicket>()
                              .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                          ?? throw new BusinessException(
                              ApiErrorCode.VersionsUploadTicketInvalid,
                              TicketInvalidMessage);

            ThrowIfUnusable(tracked, now);
            tracked.ConsumedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            InMemoryGate.Release();
        }
    }

    private async Task ThrowClassifiedAsync(
        string appKey,
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await _db.Set<VersionUploadTicket>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken);
        if (current is null)
        {
            throw new BusinessException(ApiErrorCode.VersionsUploadTicketInvalid, TicketInvalidMessage);
        }

        ThrowIfUnusable(current, now);
        throw new BusinessException(ApiErrorCode.VersionsUploadTicketConsumed, TicketConsumedMessage);
    }

    private static void ThrowIfUnusable(VersionUploadTicket ticket, DateTimeOffset now)
    {
        if (ticket.ConsumedAt is not null)
        {
            throw new BusinessException(ApiErrorCode.VersionsUploadTicketConsumed, TicketConsumedMessage);
        }

        if (ticket.ExpiresAt <= now)
        {
            throw new BusinessException(ApiErrorCode.VersionsUploadTicketExpired, TicketExpiredMessage);
        }
    }
}
