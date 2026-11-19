using System.Collections.Concurrent;
using Platform.Kernel.Abstractions;

namespace Platform.Kernel.Hubs;

/// <summary>进程内广播回执协调器：回执按（应用，备份）配对，先到者胜。</summary>
/// <remarks>
/// 多实例部署时回执会落在另一台实例上导致超时，届时换 Redis 背板实现；接口不变。
/// 当前由启动期 <c>SingleInstanceGuard</c> 阻止多副本，这个假设才有保证。
/// </remarks>
public sealed class PlatformPushCoordinator : IPlatformPushCoordinator
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PushAckResult>> _acks =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void CompleteAck(string appKey, Guid backupId, bool succeeded, string? message)
    {
        if (_acks.TryGetValue(Key(appKey, backupId), out var completion))
        {
            completion.TrySetResult(new PushAckResult(true, succeeded, message));
        }
    }

    /// <inheritdoc />
    public async Task<PushAckResult> WaitForAckAsync(
        string appKey,
        Guid backupId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var key = Key(appKey, backupId);
        var completion = _acks.GetOrAdd(
            key,
            static _ => new TaskCompletionSource<PushAckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously));

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            return await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PushAckResult(false, false, null);
        }
        finally
        {
            _acks.TryRemove(key, out _);
        }
    }

    private static string Key(string appKey, Guid backupId) => $"{appKey}|{backupId:N}";
}