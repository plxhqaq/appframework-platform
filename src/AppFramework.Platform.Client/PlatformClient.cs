using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Identity;
using Platform.Contracts.Modules;
using Platform.Contracts.Permissions;

namespace AppFramework.Platform.Client;

/// <summary>
/// 平台桌面侧客户端：M2M/用户令牌、临时登录、扫码登录、平台自述。
/// 版本、授权、文档与配置备份的调用在 <c>PlatformClient.Modules.cs</c>。
/// </summary>
public sealed partial class PlatformClient : IDisposable
{
    private const string TokenPath = "/api/v1/apps/token";
    private const string LoginPath = "/api/v1/identity/login";
    private const string RefreshPath = "/api/v1/identity/refresh";
    private const string LogoutPath = "/api/v1/identity/logout";
    private const string ChangePasswordPath = "/api/v1/identity/password/change";
    private const string SingleKeyRedeemPath = "/api/v1/identity/singlekey/redeem";
    private const string QrSessionsPath = "/api/v1/identity/qr/sessions";
    private const string ModulesPath = "/api/v1/platform/modules";
    private const string PermissionsPath = "/api/v1/platform/permissions";
    private const string WhoAmIPath = "/api/v1/platform/whoami";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// 创建客户端。传入 <paramref name="httpClient"/> 时超时等由你配置（集成测试里用
    /// <c>WebApplicationFactory.CreateClient()</c> 正是这条路），此时 <see cref="PlatformClientOptions.Timeout"/> 不生效。
    /// </summary>
    public PlatformClient(PlatformClientOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"{nameof(options.BaseUrl)} 必须是带 http/https 的绝对地址：{options.BaseUrl}", nameof(options));
        }

        Options = options;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= baseAddress;
        if (_ownsHttpClient)
        {
            _http.Timeout = options.Timeout;
        }

        AddDefaultHeader(PlatformHeaders.ClientVersion, options.ClientVersion);
        AddDefaultHeader(PlatformHeaders.RuntimeIdentifier, options.RuntimeIdentifier);
    }

    /// <summary>创建该实例时使用的配置。</summary>
    public PlatformClientOptions Options { get; }

    /// <summary>当前访问令牌；<c>null</c> 表示请求不带 Authorization 头。</summary>
    public string? AccessToken { get; private set; }

    /// <summary>访问令牌到期时间（服务端给出）。</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; private set; }

    /// <summary>
    /// 刷新令牌。可写：桌面端把它存进凭据库，下次启动回填这里，续期与自动重试即可继续工作。
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>最近一次登录/续期返回的主体（含权限码与应用键）。</summary>
    public UserSessionDto? CurrentUser { get; private set; }

    /// <summary>接管外部保存的令牌（例如从系统凭据库读回）。</summary>
    public void UseAccessToken(string accessToken, DateTimeOffset? expiresAt = null, UserSessionDto? user = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        AccessToken = accessToken;
        AccessTokenExpiresAt = expiresAt;
        CurrentUser = user;
    }

    /// <summary>丢弃全部本地凭据（不通知服务端）。</summary>
    public void ClearCredentials()
    {
        AccessToken = null;
        AccessTokenExpiresAt = null;
        RefreshToken = null;
        CurrentUser = null;
    }

    /// <summary>主体是否持有权限码（<c>*</c> 视为全部）。M2M 主体没有权限集，恒为 <c>false</c>。</summary>
    public bool HasPermission(string code)
    {
        var permissions = CurrentUser?.Permissions;
        return permissions is not null
            && (permissions.Contains(PermissionCatalog.Wildcard, StringComparer.Ordinal)
                || permissions.Contains(code, StringComparer.Ordinal));
    }

    /// <summary>
    /// 用 API Key 换 M2M 服务令牌（<c>X-App-Key</c> + <c>X-Api-Key</c>）。
    /// 成功后凭据被本客户端接管；服务令牌没有刷新令牌，到期只能再换一次。
    /// </summary>
    public async Task<M2MTokenResponse> RequestServiceTokenAsync(
        string appKey,
        string apiKey,
        IReadOnlyList<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = JsonContent.Create(new M2MTokenRequest(scopes), options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation(PlatformHeaders.AppKey, appKey);
        request.Headers.TryAddWithoutValidation(PlatformHeaders.ApiKey, apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var token = await ReadEnvelopeAsync<M2MTokenResponse>(response, cancellationToken);
        ApplyCredentials(token.AccessToken, token.ExpiresAt, refreshToken: null, user: null);
        return token;
    }

    /// <summary>用户名口令登录（桌面端刷新令牌在响应体里，由客户端自行存储）。</summary>
    public async Task<LoginResponse> SignInAsync(
        string userName,
        string password,
        string? appKey = null,
        string? deviceFingerprint = null,
        string? machineCode = null,
        CancellationToken cancellationToken = default)
    {
        var body = new LoginRequest(userName, password, appKey, deviceFingerprint, machineCode);
        var result = await CallAsync<LoginResponse>(HttpMethod.Post, LoginPath, body, useToken: false, cancellationToken);
        ApplyCredentials(result.AccessToken, result.ExpiresAt, result.RefreshToken, result.User);
        return result;
    }

    /// <summary>用刷新令牌续期；成功后旧访问令牌作废。续期失败不会被自动重试掩盖。</summary>
    public async Task<RefreshTokenResponse> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = RefreshToken ?? throw new InvalidOperationException("没有可用的刷新令牌，请重新登录。");
        var result = await CallAsync<RefreshTokenResponse>(
            HttpMethod.Post, RefreshPath, new RefreshTokenRequest(refreshToken), useToken: false, cancellationToken);
        ApplyCredentials(result.AccessToken, result.ExpiresAt, result.RefreshToken, result.User);
        return result;
    }

    /// <summary>登出并吊销刷新令牌；无论服务端结果如何，本地凭据都会被清掉。</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (RefreshToken is { Length: > 0 } refreshToken)
            {
                await CallAsync<object?>(HttpMethod.Post, LogoutPath, new LogoutRequest(refreshToken), useToken: false, cancellationToken);
            }
        }
        finally
        {
            ClearCredentials();
        }
    }

    /// <summary>修改口令。服务端会吊销全部会话，因此本地凭据同步作废，必须重新登录。</summary>
    public async Task<PasswordChangedResponse> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallAsync<PasswordChangedResponse>(
                HttpMethod.Post,
                ChangePasswordPath,
                new ChangePasswordRequest(currentPassword, newPassword),
                useToken: true,
                cancellationToken);
        }
        finally
        {
            ClearCredentials();
        }
    }

    /// <summary>兑换现场签发的 6 位单次密钥，得到临时主体令牌（原子单次：并发只有一个成功）。</summary>
    public async Task<TemporarySessionResponse> RedeemSingleKeyAsync(
        string key,
        string? deviceFingerprint = null,
        string? machineCode = null,
        CancellationToken cancellationToken = default)
    {
        var body = new RedeemSingleLoginKeyRequest(key, deviceFingerprint, machineCode);
        var result = await CallAsync<TemporarySessionResponse>(
            HttpMethod.Post, SingleKeyRedeemPath, body, useToken: false, cancellationToken);
        ApplyCredentials(result.AccessToken, result.ExpiresAt, refreshToken: null, user: result.User);
        return result;
    }

    /// <summary>发起扫码登录会话，返回 <c>requestId</c> 与二维码文本。</summary>
    public Task<QrSessionCreatedDto> CreateQrLoginSessionAsync(
        string? deviceFingerprint = null,
        string? clientName = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<QrSessionCreatedDto>(
            HttpMethod.Post, QrSessionsPath, new CreateQrSessionRequest(deviceFingerprint, clientName), useToken: false, cancellationToken);

    /// <summary>读取扫码会话状态（<c>pending</c>/<c>approved</c>/<c>rejected</c>/<c>expired</c>/<c>cancelled</c>）。</summary>
    public Task<QrSessionStatusDto> GetQrLoginSessionAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        CallAsync<QrSessionStatusDto>(HttpMethod.Get, $"{QrSessionsPath}/{requestId:D}", body: null, useToken: false, cancellationToken);

    /// <summary>
    /// 放弃本次扫码：离开二维码页、刷新码或关闭登录框时调用，服务端待审批列表随即移除该单。
    /// 幂等，已处理或已过期的会话原样返回；调用方不需要处理业务错误。
    /// </summary>
    public Task<QrSessionStatusDto> CancelQrLoginSessionAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        CallAsync<QrSessionStatusDto>(HttpMethod.Post, $"{QrSessionsPath}/{requestId:D}/cancel", body: null, useToken: false, cancellationToken);

    /// <summary>
    /// 轮询直到扫码被审批 —— 推送只是提示，客户端必须以轮询结果为准。
    /// </summary>
    /// <exception cref="TimeoutException">超过 <paramref name="timeout"/> 仍未被审批。</exception>
    public async Task<TemporarySessionResponse> WaitForQrLoginApprovalAsync(
        Guid requestId,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (true)
        {
            var status = await GetQrLoginSessionAsync(requestId, cancellationToken);
            switch (status.Status)
            {
                case QrLoginStatuses.Approved when status.Session is { } session:
                    ApplyCredentials(session.AccessToken, session.ExpiresAt, refreshToken: null, user: session.User);
                    return session;
                case QrLoginStatuses.Rejected:
                    throw new PlatformClientException(ApiErrorCode.IdentityQrSessionAlreadyUsed, "扫码登录请求被拒绝。", 0, traceId: null);
                case QrLoginStatuses.Cancelled:
                    throw new PlatformClientException(ApiErrorCode.IdentityQrSessionAlreadyUsed, "扫码登录请求已取消。", 0, traceId: null);
                case QrLoginStatuses.Expired:
                    throw new PlatformClientException(ApiErrorCode.IdentityQrSessionExpired, "扫码登录请求已过期。", 0, traceId: null);
            }

            try
            {
                await Task.Delay(interval, timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"等待扫码审批超过 {timeout}。");
            }
        }
    }

    /// <summary>模块清单（匿名可读，用于确认服务端启用了哪些模块）。</summary>
    public async Task<IReadOnlyList<ModuleInfoDto>> GetModulesAsync(CancellationToken cancellationToken = default) =>
        await CallAsync<List<ModuleInfoDto>>(HttpMethod.Get, ModulesPath, body: null, useToken: false, cancellationToken);

    /// <summary>权限码目录（客户端权限映射的唯一来源）。</summary>
    public async Task<IReadOnlyList<PermissionDescriptor>> GetPermissionsAsync(CancellationToken cancellationToken = default) =>
        await CallAsync<List<PermissionDescriptor>>(HttpMethod.Get, PermissionsPath, body: null, useToken: false, cancellationToken);

    /// <summary>当前令牌解析结果 —— 联调声明丢失时的第一站。</summary>
    public Task<WhoAmIResponse> GetWhoAmIAsync(CancellationToken cancellationToken = default) =>
        AuthorizedCallAsync<WhoAmIResponse>(HttpMethod.Get, WhoAmIPath, body: null, cancellationToken);

    /// <summary>存活探针（不查库），quickstart 的第一步。</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/healthz", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <summary>就绪探针响应原文（含数据库连通性）；能不能接流量看这里。</summary>
    public async Task<string> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/ready", cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>不带自动续期的调用：登录/续期/登出这类凭据端点自己负责结果。</summary>
    private async Task<T> CallAsync<T>(HttpMethod method, string path, object? body, bool useToken, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body, useToken, cancellationToken);
        return await ReadEnvelopeAsync<T>(response, cancellationToken);
    }

    /// <summary>业务端点调用：401 且有刷新令牌时自动续期并重试一次（只一次）。</summary>
    private async Task<T> AuthorizedCallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var first = await SendAsync(method, path, body, useToken: true, cancellationToken);
        if (first.StatusCode != HttpStatusCode.Unauthorized || RefreshToken is not { Length: > 0 })
        {
            return await ReadEnvelopeAsync<T>(first, cancellationToken);
        }

        await RefreshAsync(cancellationToken);
        using var second = await SendAsync(method, path, body, useToken: true, cancellationToken);
        return await ReadEnvelopeAsync<T>(second, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool useToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (useToken && AccessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await _http.SendAsync(request, cancellationToken);
    }

    private static async Task<T> ReadEnvelopeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var envelope = TryReadEnvelope<T>(text);
        if (envelope is null)
        {
            throw new PlatformClientException(
                response.IsSuccessStatusCode ? ApiErrorCode.Unknown : CodeForStatus(response.StatusCode),
                response.IsSuccessStatusCode ? "响应不是平台信封格式。" : $"服务端返回 HTTP {(int)response.StatusCode}。",
                (int)response.StatusCode,
                traceId: null);
        }

        return envelope.IsSuccess
            ? envelope.Data!
            : throw new PlatformClientException(
                (ApiErrorCode)envelope.Code, envelope.Message, (int)response.StatusCode, envelope.TraceId);
    }

    private static Envelope<T>? TryReadEnvelope<T>(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Envelope<T>>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ApiErrorCode CodeForStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => ApiErrorCode.Unauthorized,
        HttpStatusCode.Forbidden => ApiErrorCode.Forbidden,
        HttpStatusCode.NotFound => ApiErrorCode.NotFound,
        HttpStatusCode.Conflict => ApiErrorCode.Conflict,
        HttpStatusCode.TooManyRequests => ApiErrorCode.RateLimited,
        _ => ApiErrorCode.Unknown,
    };

    private void AddDefaultHeader(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !_http.DefaultRequestHeaders.Contains(name))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    private void ApplyCredentials(string accessToken, DateTimeOffset expiresAt, string? refreshToken, UserSessionDto? user)
    {
        AccessToken = accessToken;
        AccessTokenExpiresAt = expiresAt;
        if (refreshToken is { Length: > 0 })
        {
            RefreshToken = refreshToken;
        }

        CurrentUser = user;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private sealed record Envelope<T>(bool IsSuccess, int Code, string? Message, T? Data, string? TraceId);
}
