using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Licensing;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Licensing.Domain;
using Platform.Modules.Licensing.Options;

namespace Platform.Modules.Licensing.Services;

/// <summary>离线授权码签发、校验与台账。</summary>
public interface IOfflineLicenseCodeService
{
    /// <summary>分页查询离线码台账。</summary>
    Task<PageResult<OfflineLicenseCodeDto>> ListAsync(
        string appKey,
        PageQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>签发离线码（无联网现场）。未配置私钥抛 <see cref="ApiErrorCode.LicensingSigningKeyUnavailable"/>。</summary>
    Task<OfflineLicenseCodeDto> IssueAsync(
        string appKey,
        IssueOfflineLicenseCodeRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>校验离线码：签名、产品绑定与机器码必须全部匹配。</summary>
    Task<OfflineLicenseVerificationDto> VerifyAsync(
        string appKey,
        VerifyOfflineLicenseCodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>删除台账行；仅已过有效期（含宽限期）的记录可删——离线码无法吊销，未过期删除会失去留痕。</summary>
    Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>离线授权码实现。</summary>
public sealed class OfflineLicenseCodeService : IOfflineLicenseCodeService
{
    private readonly PlatformDbContext _db;
    private readonly ILicenseKeyService _keys;
    private readonly IClock _clock;
    private readonly LicensingOptions _options;
    private readonly IDeviceNodeDirectory? _nodes;

    /// <summary>构造。</summary>
    public OfflineLicenseCodeService(
        PlatformDbContext db,
        ILicenseKeyService keys,
        IClock clock,
        IOptions<LicensingOptions> options,
        IEnumerable<IDeviceNodeDirectory> nodes)
    {
        _db = db;
        _keys = keys;
        _clock = clock;
        _options = options.Value;
        _nodes = nodes.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<PageResult<OfflineLicenseCodeDto>> ListAsync(
        string appKey,
        PageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<OfflineLicenseCode> source = _db.Set<OfflineLicenseCode>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            string keyword = query.Keyword.Trim();
            source = source.Where(x => x.MachineCode.Contains(keyword)
                || x.LicenseNo.Contains(keyword)
                || x.Product.Key.Contains(keyword));
        }

        long total = await source.LongCountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(static x => x.CreatedAt)
            .Skip(query.Skip)
            .Take(query.ClampedPageSize)
            .Include(static x => x.Product)
            .ToListAsync(cancellationToken);

        return PageResult<OfflineLicenseCodeDto>.Create([.. rows.Select(ToDto)], total, query);
    }

    /// <inheritdoc />
    public async Task<OfflineLicenseCodeDto> IssueAsync(
        string appKey,
        IssueOfflineLicenseCodeRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_keys.IsManaged || _options.SigningKeyPkcs8 is not { Length: > 0 } privateKey)
        {
            throw new BusinessException(
                ApiErrorCode.LicensingSigningKeyUnavailable,
                "本部署未配置签发私钥（Licensing:SigningKeyPkcs8），无法签发离线授权码。");
        }

        var now = _clock.UtcNow;
        string productKey = request.ProductKey?.Trim() ?? string.Empty;
        if (productKey.Length == 0)
        {
            throw BusinessException.Invalid("产品键不能为空。");
        }

        var product = await _db.Set<LicenseProduct>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Key == productKey, cancellationToken)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.LicensingProductNotFound,
                $"产品 {productKey} 不存在。");

        if (!product.IsEnabled)
        {
            throw BusinessException.Invalid($"产品 {productKey} 已停用，不能签发离线码。");
        }

        string machineCode = await ResolveMachineCodeAsync(request.NodeId, request.MachineCode, cancellationToken);

        DateTimeOffset notAfter;
        if (request.NotAfter is { } explicitDate)
        {
            notAfter = explicitDate.ToUniversalTime();
        }
        else
        {
            int max = Math.Clamp(_options.MaxLicenseDays, 1, LicenseConventions.MaxLicenseDays);
            int days = request.AddDays ?? LicenseConventions.DefaultLicenseDays;
            if (days is < 1 || days > max)
            {
                throw BusinessException.Invalid($"授权天数必须在 1–{max} 之间。");
            }

            notAfter = now.AddDays(days);
        }

        if (notAfter <= now)
        {
            throw BusinessException.Invalid("到期时间必须晚于当前时间。");
        }

        string licenseNo = NormalizeLicenseNo(request.LicenseNo, now, machineCode);

        // 离线码不含宽限：码一旦发出就无人可控；宽限只属于可在线吊销的授权。
        const int graceDays = 0;
        string code;
        try
        {
            code = OfflineLicenseCodec.Encode(
                appKey,
                product.Key,
                machineCode,
                LicenseConventions.KindOfficial,
                now,
                notAfter,
                graceDays,
                privateKey,
                licenseNo);
        }
        catch (ArgumentException exception)
        {
            throw BusinessException.Invalid(exception.Message);
        }

        var ledger = new OfflineLicenseCode
        {
            AppKey = appKey,
            ProductId = product.Id,
            MachineCode = machineCode,
            LicenseNo = licenseNo,
            Code = code,
            Kind = LicenseConventions.KindOfficial,
            IssuedAt = now,
            NotAfter = notAfter,
            GraceDays = graceDays,
            Remark = NormalizeText(request.Remark, 512),
            IssuedBy = actor,
        };

        _db.Set<OfflineLicenseCode>().Add(ledger);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(
                ApiErrorCode.Conflict,
                $"授权编号 {licenseNo} 已存在，请换一个编号或留空自动生成。",
                exception);
        }

        ledger.Product = product;
        return ToDto(ledger);
    }

    /// <inheritdoc />
    public async Task<OfflineLicenseVerificationDto> VerifyAsync(
        string appKey,
        VerifyOfflineLicenseCodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!LicenseConventions.TryNormalizeMachineCode(request.MachineCode, out string machineCode))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingMachineCodeMissing,
                $"机器码不合法（应为 {LicenseConventions.MachineCodeChars} 字符 Crockford Base32）。");
        }

        IReadOnlyDictionary<string, string> trusted = await _keys.GetTrustedKeysAsync(cancellationToken);
        if (!OfflineLicenseCodec.TryParse(request.Code, [.. trusted.Values], out OfflineLicenseCodeContent content, out string? error))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingOfflineCodeInvalid,
                error ?? "离线授权码不合法。");
        }

        if (!string.Equals(content.AppKey, appKey, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingOfflineCodeInvalid,
                $"离线授权码属于应用 {content.AppKey}，不能在当前应用使用。");
        }

        string? requestedAppKey = request.AppKey?.Trim();
        if (!string.IsNullOrEmpty(requestedAppKey)
            && !string.Equals(requestedAppKey, appKey, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingOfflineCodeInvalid,
                "离线授权码与请求指定的应用不一致。");
        }

        if (!string.Equals(content.MachineCode, machineCode, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingOfflineCodeMachineMismatch,
                "离线授权码绑定的是另一台机器，不能在本机使用。");
        }

        if (!string.IsNullOrWhiteSpace(request.ProductKey)
            && !string.Equals(content.ProductKey, request.ProductKey.Trim(), StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingOfflineCodeInvalid,
                "离线授权码不属于该产品。");
        }

        LicenseProduct? product = await _db.Set<LicenseProduct>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.Key == content.ProductKey,
                cancellationToken);
        if (product is null)
        {
            throw BusinessException.NotFound(
                ApiErrorCode.LicensingProductNotFound,
                $"离线授权码对应的产品 {content.ProductKey} 不存在。");
        }

        if (!product.IsEnabled)
        {
            throw BusinessException.Invalid($"产品 {product.Key} 已停用，离线授权码不可用。");
        }

        if (_clock.UtcNow > content.NotAfter.AddDays(content.GraceDays))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingEntitlementExpired,
                "离线授权码已过期。");
        }

        return new OfflineLicenseVerificationDto(
            IsValid: true,
            content.ProductKey,
            content.MachineCode,
            content.Kind,
            content.IssuedAt,
            content.NotAfter,
            content.GraceDays,
            content.LicenseNo,
            content.AppKey);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default)
    {
        // 不用 ExecuteDelete：InMemory 提供程序不支持批量删除，集成测试会直接 500
        OfflineLicenseCode? row = await _db.Set<OfflineLicenseCode>()
            .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken);
        if (row is null)
        {
            throw BusinessException.NotFound(
                ApiErrorCode.NotFound,
                $"离线授权码记录 {id} 不存在。");
        }

        if (row.NotAfter.AddDays(row.GraceDays) >= _clock.UtcNow)
        {
            throw new BusinessException(
                ApiErrorCode.Conflict,
                "离线授权码尚未过期（含宽限期），过期后才可删除记录。");
        }

        _db.Set<OfflineLicenseCode>().Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> ResolveMachineCodeAsync(
        Guid? nodeId,
        string? machineCode,
        CancellationToken cancellationToken)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(machineCode))
        {
            normalized = LicenseConventions.TryNormalizeMachineCode(machineCode, out string value)
                ? value
                : throw new BusinessException(
                    ApiErrorCode.LicensingMachineCodeMissing,
                    $"机器码不合法（应为 {LicenseConventions.MachineCodeChars} 字符 Crockford Base32）。");
        }

        if (nodeId is not { } id || id == Guid.Empty)
        {
            return normalized ?? throw new BusinessException(
                ApiErrorCode.LicensingMachineCodeMissing,
                "必须提供 NodeId 或 MachineCode。");
        }

        if (_nodes is null)
        {
            throw new BusinessException(
                ApiErrorCode.DependencyFailure,
                "设备域未启用，无法按 NodeId 签发离线码；请直接提供 MachineCode。");
        }

        DeviceNodeDirectoryEntry node = await _nodes.FindAsync(id, cancellationToken)
            ?? throw BusinessException.NotFound(ApiErrorCode.DevicesNodeNotFound, $"节点 {id} 不存在。");

        if (normalized is not null && !string.Equals(normalized, node.MachineCode, StringComparison.Ordinal))
        {
            throw BusinessException.Invalid("NodeId 与 MachineCode 指向不同的机器。");
        }

        return node.MachineCode;
    }

    private static string NormalizeLicenseNo(string? licenseNo, DateTimeOffset now, string machineCode)
    {
        if (string.IsNullOrWhiteSpace(licenseNo))
        {
            return LicenseNumbers.Generate(now, machineCode);
        }

        string trimmed = licenseNo.Trim();
        if (trimmed.Length > LicenseConventions.MaxLicenseNoChars)
        {
            throw BusinessException.Invalid(
                $"授权编号不能超过 {LicenseConventions.MaxLicenseNoChars} 个字符。");
        }

        if (trimmed.Any(static c => c is < ' ' or > '~'))
        {
            throw BusinessException.Invalid("授权编号只能包含可打印 ASCII 字符。");
        }

        return trimmed;
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

    private static OfflineLicenseCodeDto ToDto(OfflineLicenseCode ledger) =>
        new(
            ledger.Id,
            ledger.Product?.Key ?? string.Empty,
            ledger.MachineCode,
            ledger.LicenseNo,
            ledger.Code,
            Base32Crockford.FormatGroups(ledger.Code, 8),
            ledger.Kind,
            ledger.IssuedAt,
            ledger.NotAfter,
            ledger.GraceDays,
            ledger.Remark,
            ledger.IssuedBy,
            ledger.CreatedAt);
}
