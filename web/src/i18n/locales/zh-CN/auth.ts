export const authZh = {
  login: {
    title: 'AppFramework 平台',
    subtitle: '通用服务提供平台 · 管理台',
    returnHint: '登录后会回到你刚才打开的页面',
    userName: '用户名',
    password: '密码',
    submit: '登录',
    userNameRequired: '请输入用户名',
    passwordRequired: '请输入密码',
    failed: '登录失败',
    mustChangePassword: '首次登录或口令已被重置，请先修改口令',
  },
  changePassword: {
    title: '修改口令',
    description: '首次登录或口令被重置后需要先改口令；改完会吊销其他会话。',
    current: '当前口令',
    next: '新口令',
    confirm: '确认新口令',
    submit: '保存新口令',
    currentRequired: '请输入当前口令',
    nextRequired: '请输入新口令',
    confirmRequired: '请再次输入新口令',
    mismatch: '两次输入的新口令不一致',
    success: '口令已修改，请用新口令重新登录',
  },
  denied: {
    title: '没有访问该页面的权限',
    description:
      '当前账号的权限里没有这个页面需要的权限码。需要的话请让管理员在「身份与权限 → 角色」里补上。',
    qrDescription:
      '审批扫码登录需要 identity.qr.approve 权限。现场工程师账号默认就有；临时登录（密钥/扫码换来的会话）不能审批别人的扫码。',
    backHome: '返回总览',
  },
};
