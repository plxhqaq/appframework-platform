using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Licensing;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Licensing.Domain;

namespace Platform.Modules.Licensing.Services;

/// <summary>授权产品读写。</summary>
public interface ILicenseProductService
{
    /// <summary>列出当前应用的产品（含授权计数）。</summary>
    Task<IReadOnlyList<LicenseProductDto>> ListAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>创建产品；键重复抛 <see cref="ApiErrorCode.LicensingProductKeyExists"/>。</summary>
    Task<LicenseProductDto> CreateAsync(
        string appKey,
        CreateLicenseProductRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>更新产品；停用后不再签发与校验。</summary>
    Task<LicenseProductDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateLicenseProductRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>授权产品实现。</summary>
public sealed partial class LicenseProductService : ILicenseProductService
{
    private readonly PlatformDbContext _db;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public LicenseProductService(PlatformDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    [GeneratedRegex(LicenseConventions.ProductKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ProductKeyRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<LicenseProductDto>> ListAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var rows = await _db.Set<LicenseProduct>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey)
            .OrderBy(static x => x.Key)
            .Select(x => new
            {
                Product = x,
                Total = x.Entitlements.Count,
                Active = x.Entitlements.Count(e => e.RevokedAt == null && e.ExpiresAt >= now),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(static row => ToDto(row.Product, row.Total, row.Active))];
    }

    /// <inheritdoc />
    public async Task<LicenseProductDto> CreateAsync(
        string appKey,
        CreateLicenseProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string key = request.Key?.Trim() ?? string.Empty;
        if (!ProductKeyRegex().IsMatch(key))
        {
            throw BusinessException.Invalid(
                $"产品键必须匹配 {LicenseConventions.ProductKeyPattern}（小写字母开头，2–32 位小写字母/数字/连字符）。");
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw BusinessException.Invalid("产品名称不能为空。");
        }

        int graceDays = NormalizeGraceDays(request.DefaultGraceDays);
        int trialDays = NormalizeTrialDays(request.TrialDays);
        int? seatLimit = NormalizeSeatLimit(request.SeatLimit);

        if (await _db.Set<LicenseProduct>()
                .AnyAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingProductKeyExists,
                $"产品键 {key} 已存在。");
        }

        var product = new LicenseProduct
        {
            AppKey = appKey,
            Key = key,
            Name = name,
            Description = NormalizeText(request.Description, 1024),
            SeatLimit = seatLimit,
            DefaultGraceDays = graceDays,
            TrialDays = trialDays,
        };

        _db.Set<LicenseProduct>().Add(product);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(
                ApiErrorCode.LicensingProductKeyExists,
                $"产品键 {key} 已存在。",
                exception);
        }

        return ToDto(product, 0, 0);
    }

    /// <inheritdoc />
    public async Task<LicenseProductDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateLicenseProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await _db.Set<LicenseProduct>()
                          .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                      ?? throw BusinessException.NotFound(
                          ApiErrorCode.LicensingProductNotFound,
                          $"授权产品 {id} 不存在。");

        if (request.Name is not null)
        {
            string name = request.Name.Trim();
            if (name.Length == 0)
            {
                throw BusinessException.Invalid("产品名称不能为空。");
            }

            product.Name = name;
        }

        if (request.Description is not null)
        {
            product.Description = NormalizeText(request.Description, 1024);
        }

        if (request.IsEnabled is { } enabled)
        {
            product.IsEnabled = enabled;
        }

        if (request.SeatLimit is { } seatLimit)
        {
            product.SeatLimit = NormalizeSeatLimit(seatLimit);
        }

        if (request.DefaultGraceDays is { } graceDays)
        {
            product.DefaultGraceDays = NormalizeGraceDays(graceDays);
        }

        if (request.TrialDays is { } trialDays)
        {
            product.TrialDays = NormalizeTrialDays(trialDays);
        }

        await _db.SaveChangesAsync(cancellationToken);

        var now = _clock.UtcNow;
        int total = await _db.Set<LicenseEntitlement>()
            .CountAsync(x => x.ProductId == product.Id, cancellationToken);
        int active = await _db.Set<LicenseEntitlement>()
            .CountAsync(x => x.ProductId == product.Id && x.RevokedAt == null && x.ExpiresAt >= now, cancellationToken);

        return ToDto(product, total, active);
    }

    private static int NormalizeGraceDays(int value)
    {
        if (value is < 0 or > LicenseConventions.MaxGraceDays)
        {
            throw BusinessException.Invalid($"宽限天数必须在 0–{LicenseConventions.MaxGraceDays} 之间。");
        }

        return value;
    }

    private static int NormalizeTrialDays(int value)
    {
        if (value is < 0 or > LicenseConventions.MaxTrialDays)
        {
            throw BusinessException.Invalid($"试用天数必须在 0–{LicenseConventions.MaxTrialDays} 之间。");
        }

        return value;
    }

    private static int? NormalizeSeatLimit(int? value)
    {
        if (value is null or <= 0)
        {
            return null;
        }

        if (value > LicenseConventions.MaxSeatLimit)
        {
            throw BusinessException.Invalid($"席位上限不能超过 {LicenseConventions.MaxSeatLimit}。");
        }

        return value;
    }

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw BusinessException.Invalid($"文本长度不能超过 {maxLength} 个字符。");
        }

        return trimmed;
    }

    private static LicenseProductDto ToDto(LicenseProduct product, int total, int active) =>
        new(
            product.Id,
            product.Key,
            product.Name,
            product.Description,
            product.IsEnabled,
            product.SeatLimit,
            product.DefaultGraceDays,
            product.TrialDays,
            total,
            active,
            product.CreatedAt,
            product.UpdatedAt);
}
