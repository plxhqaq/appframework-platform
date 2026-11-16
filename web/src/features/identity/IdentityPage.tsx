import { tr } from '../../i18n/translate';
import { Alert, Card, Tabs } from 'antd';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { RolesPanel } from './RolesPanel';
import { SessionsPanel } from './SessionsPanel';
import { UsersPanel } from './UsersPanel';

/**
 * 身份与权限页：账号、角色、会话。
 *
 * 密钥登录与扫码审批已经拆成独立页面（现场工程师只用那两个，不该先翻到这里）。
 * 标签页按读权限裁剪；隐藏只是不打扰用户，真正的越权请求由服务端拒绝。
 */
export function IdentityPage() {
  const canReadUsers = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityUserRead));
  const canReadRoles = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityRoleRead));
  const canReadSessions = useAuthStore((state) => state.hasPermission(PERMISSIONS.identitySessionRead));

  const items = [
    canReadUsers ? { key: 'users', label: tr("用户"), children: <UsersPanel /> } : null,
    canReadRoles ? { key: 'roles', label: tr("角色"), children: <RolesPanel /> } : null,
    canReadSessions ? { key: 'sessions', label: tr("会话"), children: <SessionsPanel /> } : null,
  ].filter((item) => item !== null);

  return (
    <Card title={tr("身份与权限")} size="small">
      {items.length > 0 ? (
        <Tabs items={items} />
      ) : (
        <Alert
          type="warning"
          showIcon
          message={tr("当前账号没有身份管理相关权限")}
          description={tr("需要 identity.user.read、identity.role.read 或 identity.session.read 之一。")}
        />
      )}
    </Card>
  );
}
