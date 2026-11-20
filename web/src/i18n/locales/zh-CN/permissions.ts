export const permissionsZh = {
  modules: {
    apps: '应用接入',
    identity: '身份与权限',
    versions: '版本与渠道',
    licensing: '授权',
    devices: '安装实例',
    backups: '备份',
    documents: '文档',
    webhooks: 'Webhook 订阅',
    platform: '平台与节点',
  },
  wildcard: '通配符：拥有全部权限（仅内置平台管理员角色）',
  codes: {
    apps: {
      read: '查看应用列表、应用详情与模块装配情况',
      manage: '新建、编辑、启停应用及其应用键',
      apikey: { manage: '签发与吊销应用 API 密钥，决定设备或集成能换取的权限范围' },
      permissions: { manage: '解锁或手动锁定应用的权限清单，决定客户端能否再次上传覆盖' },
    },
    identity: {
      user: {
        read: '查看用户列表与用户详情',
        manage: '新建、编辑、启停用户并分配角色',
        password: { reset: '重置其他用户口令（会吊销该用户全部会话）' },
      },
      role: { read: '查看角色及其权限清单', manage: '新建、编辑、删除角色并调整角色权限' },
      session: {
        read: '查看活跃会话与刷新令牌状态',
        revoke: '强制吊销指定会话，使用方需要重新登录',
      },
      singlekey: { issue: '签发 6 位一次性登录口令，供现场临时登录' },
      qr: { approve: '批准或拒绝扫码登录请求' },
    },
    versions: {
      channel: { read: '查看发布渠道', manage: '新建、编辑、归档发布渠道' },
      release: {
        read: '查看版本、发布记录与文件清单；客户端检查更新也用它',
        publish: '创建并发布版本，控制客户端可见的更新',
        archive: '归档并停用某个已发布版本',
        delete: '丢弃草稿版本、彻底删除归档版本与已归档更新器，并回收其安装包与文件存储',
      },
      artifact: {
        upload: '上传发布产物、安装包与增量包',
        download: '下载发布文件、安装包与更新器',
      },
    },
    license: {
      product: { read: '查看授权产品定义', manage: '新建、编辑授权产品' },
      read: '查看授权实例、离线码台账与客户端授权状态',
      issue: '为指定产品与机器签发授权',
      revoke: '吊销已签发授权，客户端下次续期即失效',
      offlinecode: { issue: '生成离线授权码，供无网设备验签' },
      signingkey: { manage: '管理授权签名密钥（生成、轮换、退役），影响全部离线验签' },
    },
    backup: {
      namespace: { read: '查看备份命名空间与配额', manage: '新建、编辑、删除备份命名空间' },
      read: '查看备份列表与元数据并下载内容（含安装实例回拉下发备份）',
      create: '新建备份，或由客户端上传配置备份',
      push: '向在线安装实例下发配置备份并等待回执',
      delete: '删除备份（软删，按保留策略回收）',
    },
    document: {
      collection: { read: '查看文档集合', manage: '新建、编辑、删除文档集合' },
      read: '查看并下载文档；客户端只能看到对客户端可见的文档',
      manage: '上传、编辑文档及其版本',
      delete: '删除文档',
    },
    webhook: { read: '查看 Webhook 订阅与投递记录', manage: '新建、编辑、删除 Webhook 订阅及签名密钥' },
    node: {
      read: '查看物理节点列表、节点详情与跨应用安装实例',
      manage: '修改节点自定义名，启用或停用节点（停用即全应用拒绝）',
      restart: '重启节点：向该节点全部在线安装实例下发重启命令',
    },
    installation: {
      read: '查看本应用安装实例列表、在线状态与详情',
      manage: '启用或停用安装实例（节点停用时仍无法注册）',
      restart: '向在线安装实例下发重启命令（离线或停用会被拒绝）',
    },
    audit: { read: '查看审计日志与操作记录' },
    platform: {
      admin: '平台管理员标记位，仅内置管理员角色使用；实际放行依靠通配符',
      apidocs: { read: '查看管理台菜单底部的 Scalar API 文档入口' },
    },
  },
};
