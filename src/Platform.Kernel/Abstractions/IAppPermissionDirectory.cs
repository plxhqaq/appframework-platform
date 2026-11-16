using Platform.Contracts.Permissions;

namespace Platform.Kernel.Abstractions;

/// <summary>
/// 应用权限目录：读取各应用通过权限清单声明的权限码。
/// 平台内核不定义任何客户端专属权限，身份模块与应用接入模块只依赖本抽象。
/// </summary>
public interface IAppPermissionDirectory
{
    /// <summary>列出应用声明的权限描述符（按排序值升序）；应用不存在时抛业务 404。</summary>
    Task<IReadOnlyList<PermissionDescriptor>> ListAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>应用声明的权限码集合；应用不存在或未声明时返回空集合。</summary>
    Task<IReadOnlySet<string>> ListCodesAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>全部应用声明的权限码集合（权限码全局唯一，用于平台角色的历史兼容校验）。</summary>
    Task<IReadOnlySet<string>> ListAllCodesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 权限清单是否已锁定；应用不存在时返回 <c>false</c>。
    /// 客户端连接成功时据此判断是否还需要上传清单，避免重复调用。
    /// </summary>
    Task<bool> IsLockedAsync(string appKey, CancellationToken cancellationToken = default);
}
