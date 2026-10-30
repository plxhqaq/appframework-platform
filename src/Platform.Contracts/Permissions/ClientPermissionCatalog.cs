namespace Platform.Contracts.Permissions;

/// <summary>
/// 客户端侧权限码 + 与 WPF/Avalonia 客户端 <c>PermissionType</c> 枚举名的映射表。
/// </summary>
/// <remarks>
/// 为什么要这张表：客户端历史上用 <c>PermissionType</c> 枚举位表示权限，平台侧用字符串码。
/// 直接让两端各存一套等于双份事实源，必然漂移。因此：
/// <list type="bullet">
/// <item>平台发放的 JWT <c>perms</c> 声明里只出现字符串码；</item>
/// <item>客户端启动时用 <see cref="TypeToCode"/> 把枚举位翻译成权限码再做判定；</item>
/// <item>CI 断言「每个 <c>PermissionType</c> 成员都能在本表找到映射」，缺失即红。</item>
/// </list>
/// 多个枚举成员映射到同一权限码是**有意为之**（例如 Add/Edit/DeleteUser 同属用户管理）。
/// </remarks>
public static class ClientPermissionCatalog
{
    // ━━━ 通用操作 ━━━
    public const string ActionSave = "client.action.save";
    public const string ActionDelete = "client.action.delete";
    public const string ActionAdd = "client.action.add";
    public const string ActionReset = "client.action.reset";
    public const string ActionExport = "client.action.export";
    public const string ActionImport = "client.action.import";
    public const string ActionQuery = "client.action.query";

    // ━━━ 功能模块 ━━━
    public const string DeviceDebugOperate = "client.device.debug.operate";
    public const string CounterReset = "client.counter.reset";
    public const string LogClear = "client.log.clear";
    public const string SettingSoftwareEdit = "client.setting.software.edit";
    public const string SettingLoginEdit = "client.setting.login.edit";
    public const string SettingServerPasswordView = "client.setting.serverpassword.view";
    public const string DataHistoryClean = "client.data.history.clean";
    public const string DataTestFilesClean = "client.data.testfiles.clean";
    public const string DataCompact = "client.data.compact";
    public const string AlarmUnlock = "client.alarm.unlock";

    // ━━━ 页面访问（控制侧栏显隐；服务端不据此放行任何数据）━━━
    public const string PageArchive = "client.page.archive";
    public const string PageDeviceDebug = "client.page.devicedebug";
    public const string PageDataStatistics = "client.page.datastatistics";
    public const string PageRecordQuery = "client.page.recordquery";
    public const string PageSoftwareSetting = "client.page.softwaresetting";
    public const string PageConnectionSetting = "client.page.connectionsetting";
    public const string PageHardwareSetting = "client.page.hardwaresetting";
    public const string PageDataManagement = "client.page.datamanagement";
    public const string PageBackup = "client.page.backup";
    public const string PageDocuments = "client.page.documents";
    public const string PageAbout = "client.page.about";

    // ━━━ 客户端接入（服务端以此限制客户端入口）━━━
    public const string StationOperate = "client.station.operate";
    public const string ConfigSync = "client.config.sync";

    private const string ClientModule = "client";

    /// <summary>客户端侧全部权限描述符。</summary>
    public static IReadOnlyList<PermissionDescriptor> Permissions { get; } =
    [
        new(ActionSave, ClientModule, "perm.client.action.save", 1000),
        new(ActionDelete, ClientModule, "perm.client.action.delete", 1010),
        new(ActionAdd, ClientModule, "perm.client.action.add", 1020),
        new(ActionReset, ClientModule, "perm.client.action.reset", 1030),
        new(ActionExport, ClientModule, "perm.client.action.export", 1040),
        new(ActionImport, ClientModule, "perm.client.action.import", 1050),
        new(ActionQuery, ClientModule, "perm.client.action.query", 1060),

        new(DeviceDebugOperate, ClientModule, "perm.client.device.debug.operate", 1100),
        new(CounterReset, ClientModule, "perm.client.counter.reset", 1110),
        new(LogClear, ClientModule, "perm.client.log.clear", 1120),
        new(SettingSoftwareEdit, ClientModule, "perm.client.setting.software.edit", 1130),
        new(SettingLoginEdit, ClientModule, "perm.client.setting.login.edit", 1140),
        new(SettingServerPasswordView, ClientModule, "perm.client.setting.serverpassword.view", 1150),
        new(DataHistoryClean, ClientModule, "perm.client.data.history.clean", 1160),
        new(DataTestFilesClean, ClientModule, "perm.client.data.testfiles.clean", 1170),
        new(DataCompact, ClientModule, "perm.client.data.compact", 1180),
        new(AlarmUnlock, ClientModule, "perm.client.alarm.unlock", 1190),

        new(PageArchive, ClientModule, "perm.client.page.archive", 1200),
        new(PageDeviceDebug, ClientModule, "perm.client.page.devicedebug", 1210),
        new(PageDataStatistics, ClientModule, "perm.client.page.datastatistics", 1220),
        new(PageRecordQuery, ClientModule, "perm.client.page.recordquery", 1230),
        new(PageSoftwareSetting, ClientModule, "perm.client.page.softwaresetting", 1240),
        new(PageConnectionSetting, ClientModule, "perm.client.page.connectionsetting", 1250),
        new(PageHardwareSetting, ClientModule, "perm.client.page.hardwaresetting", 1260),
        new(PageDataManagement, ClientModule, "perm.client.page.datamanagement", 1270),
        new(PageBackup, ClientModule, "perm.client.page.backup", 1280),
        new(PageDocuments, ClientModule, "perm.client.page.documents", 1290),
        new(PageAbout, ClientModule, "perm.client.page.about", 1300),

        new(StationOperate, ClientModule, "perm.client.station.operate", 1400),
        new(ConfigSync, ClientModule, "perm.client.config.sync", 1410),
    ];

    /// <summary>
    /// 客户端 <c>PermissionType</c> 枚举成员的**完整清单**（跨仓库契约）。
    /// 服务端 CI 断言本表与 <see cref="TypeToCode"/> 键集完全一致；
    /// 客户端 CI 断言其 <c>PermissionType</c> 成员名与本表完全一致。两边都红不了才是真一致。
    /// </summary>
    public static IReadOnlyList<string> ExpectedPermissionTypeNames { get; } =
    [
        "SaveButton",
        "DeleteButton",
        "AddNewButton",
        "ResetButton",
        "ExportButton",
        "ImportButton",
        "QueryButton",
        "AddNewUser",
        "EditUser",
        "DeleteUser",
        "AddNewRole",
        "EditRole",
        "DeleteRole",
        "ResetPassword",
        "DeviceDebugOperate",
        "ResetCounter",
        "ClearSystemLog",
        "EditSoftwareSetting",
        "CleanHistoryData",
        "CleanTestDataFiles",
        "CompactDatabase",
        "EditLoginSetting",
        "ViewServerPassword",
        "ViewArchiveSetting",
        "ViewDeviceDebug",
        "ViewDataStatistics",
        "ViewRecordQuery",
        "ViewSoftwareSetting",
        "ViewConnectionSetting",
        "ViewHardwareSetting",
        "ViewDataManagement",
        "ViewBackupSetting",
        "ViewDocuments",
        "ViewAbout",
        "UnlockAlarmDialog",
    ];

    /// <summary>
    /// 客户端 <c>PermissionType</c> 成员名 → 平台权限码。
    /// 键必须与客户端枚举成员名**逐字一致**（区分大小写），由契约测试断言覆盖度。
    /// </summary>
    public static IReadOnlyDictionary<string, string> TypeToCode { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SaveButton"] = ActionSave,
            ["DeleteButton"] = ActionDelete,
            ["AddNewButton"] = ActionAdd,
            ["ResetButton"] = ActionReset,
            ["ExportButton"] = ActionExport,
            ["ImportButton"] = ActionImport,
            ["QueryButton"] = ActionQuery,

            // 用户/角色管理直接复用平台权限码：同一动作在后台与客户端不得出现两套判据
            ["AddNewUser"] = PermissionCatalog.IdentityUserManage,
            ["EditUser"] = PermissionCatalog.IdentityUserManage,
            ["DeleteUser"] = PermissionCatalog.IdentityUserManage,
            ["AddNewRole"] = PermissionCatalog.IdentityRoleManage,
            ["EditRole"] = PermissionCatalog.IdentityRoleManage,
            ["DeleteRole"] = PermissionCatalog.IdentityRoleManage,
            ["ResetPassword"] = PermissionCatalog.IdentityUserPasswordReset,

            ["DeviceDebugOperate"] = DeviceDebugOperate,
            ["ResetCounter"] = CounterReset,
            ["ClearSystemLog"] = LogClear,
            ["EditSoftwareSetting"] = SettingSoftwareEdit,
            ["EditLoginSetting"] = SettingLoginEdit,
            ["ViewServerPassword"] = SettingServerPasswordView,
            ["CleanHistoryData"] = DataHistoryClean,
            ["CleanTestDataFiles"] = DataTestFilesClean,
            ["CompactDatabase"] = DataCompact,
            ["UnlockAlarmDialog"] = AlarmUnlock,

            ["ViewArchiveSetting"] = PageArchive,
            ["ViewDeviceDebug"] = PageDeviceDebug,
            ["ViewDataStatistics"] = PageDataStatistics,
            ["ViewRecordQuery"] = PageRecordQuery,
            ["ViewSoftwareSetting"] = PageSoftwareSetting,
            ["ViewConnectionSetting"] = PageConnectionSetting,
            ["ViewHardwareSetting"] = PageHardwareSetting,
            ["ViewDataManagement"] = PageDataManagement,
            ["ViewBackupSetting"] = PageBackup,
            ["ViewDocuments"] = PageDocuments,
            ["ViewAbout"] = PageAbout,
        };

    /// <summary>反向索引：权限码 → 客户端枚举成员名（可能多个）。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CodeToTypes { get; } =
        TypeToCode
            .GroupBy(static kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(
                static g => g.Key,
                static g => (IReadOnlyList<string>)[.. g.Select(static kv => kv.Key).OrderBy(static n => n, StringComparer.Ordinal)],
                StringComparer.Ordinal);

    /// <summary>把客户端 <c>PermissionType</c> 成员名翻译为权限码；未登记返回 <c>null</c>。</summary>
    public static string? ToPermissionCode(string? permissionTypeName) =>
        permissionTypeName is not null && TypeToCode.TryGetValue(permissionTypeName, out var code) ? code : null;

    /// <summary>判断已授予权限码集合是否满足某客户端枚举成员（自动处理通配符）。</summary>
    public static bool Allows(IReadOnlySet<string>? grantedCodes, string permissionTypeName)
    {
        if (grantedCodes is null || grantedCodes.Count == 0) return false;
        if (grantedCodes.Contains(PermissionCatalog.Wildcard)) return true;
        var code = ToPermissionCode(permissionTypeName);
        return code is not null && grantedCodes.Contains(code);
    }
}
