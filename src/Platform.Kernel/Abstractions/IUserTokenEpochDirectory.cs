namespace Platform.Kernel.Abstractions;

/// <summary>
/// 用户令牌代号目录（D7 epoch 守卫）：读取 <c>users.token_epoch</c>。
/// 由 Identity 模块实现；Kernel 守卫只消费当前值，不关心 bump 时机。
/// </summary>
public interface IUserTokenEpochDirectory
{
    /// <summary>取用户当前代号；用户不存在时返回 <c>null</c>（守卫按“无法核验”放行，吊销轴由会话表覆盖）。</summary>
    Task<int?> GetEpochAsync(Guid userId, CancellationToken cancellationToken = default);
}
