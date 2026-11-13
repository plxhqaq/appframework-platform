using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;

namespace Platform.Infrastructure.Deployment;

/// <summary>
/// 单实例守卫：用 PostgreSQL 会话级 advisory lock 阻止两个进程共用同一数据库。
/// </summary>
/// <remarks>
/// Webhook 投递没有行级抢占，备份回执协调器与 SignalR 连接表都是进程内状态，多副本会重复外呼、
/// 丢回执或推送落空。与其让第二个副本悄悄跑起来，不如启动即失败。
/// 锁挂在专用连接上，进程崩溃或正常退出都会随会话释放，不会留下僵尸锁。
/// </remarks>
public sealed class SingleInstanceGuard
{
    /// <summary>平台占用的 advisory lock key（ASCII "PLTF"）。</summary>
    public const long AdvisoryLockKey = 0x504C5446;

    private readonly PlatformDbContext _db;
    private DbConnection? _connection;

    /// <summary>构造。</summary>
    public SingleInstanceGuard(PlatformDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 尝试持锁。非关系型提供程序（集成测试的 InMemory）没有 advisory lock，直接返回
    /// <c>false</c> 表示未校验，而不是让测试宿主启动失败。
    /// </summary>
    /// <exception cref="InvalidOperationException">已有另一个活动实例持有同一把锁。</exception>
    public async Task<bool> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var database = _db.Database;
        if (!database.IsNpgsql())
        {
            return false;
        }

        var connection = database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var acquired = await ExecuteScalarAsync(
            connection,
            "SELECT pg_try_advisory_lock(@key)",
            cancellationToken);

        if (acquired is not true)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                $"另一个平台实例已持有单实例锁（PostgreSQL advisory lock {AdvisoryLockKey}）。"
                + "当前版本要求单副本部署：Webhook 投递没有行级抢占，SignalR 推送与备份回执协调器是进程内状态，"
                + "多副本会导致重复投递、回执超时或推送落空。若确需扩容，请先实现投递行级认领与 SignalR 背板，"
                + "再移除本检查；单副本部署请确认旧实例已退出后重启。");
        }

        _connection = connection;
        return true;
    }

    /// <summary>释放锁并关闭专用连接；未持锁时为空操作。</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        _connection = null;
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await ExecuteScalarAsync(
                    connection,
                    "SELECT pg_advisory_unlock(@key)",
                    cancellationToken);
            }
        }
        catch (Exception)
        {
            // 解锁失败不影响正确性：连接关闭时会话级锁自动释放。
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task<object?> ExecuteScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = AdvisoryLockKey;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
