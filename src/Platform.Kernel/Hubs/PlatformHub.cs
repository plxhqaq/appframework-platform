using Microsoft.AspNetCore.SignalR;

namespace Platform.Kernel.Hubs;

/// <summary>
/// 平台唯一 SignalR Hub：业务用事件名区分。**所有推送都只是提示**，
/// 客户端收到后必须回拉接口重新校验，不得把推送内容当作事实来源。
/// </summary>
public sealed class PlatformHub : Hub
{
}
