import { Alert, Card, Tabs } from 'antd';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { QrApprovalPanel } from './QrApprovalPanel';
import { RolesPanel } from './RolesPanel';
import { SessionsPanel } from './SessionsPanel';
import { SingleLoginKeyPanel } from './SingleLoginKeyPanel';
import { UsersPanel } from './UsersPanel';

/**
 * 身份与权限页。
 *
 * 标签页按读权限裁剪；隐藏只是不打扰用户，真正的越权请求由服务端拒绝。
 */
export function IdentityPage() {
  const canReadUsers = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityUserRead));
  const canReadRoles = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityRoleRead));
  const canReadSessions = useAuthStore((state) => state.hasPermission(PERMISSIONS.identitySessionRead));
  const canIssueKey = useAuthStore((state) => state.hasPermission(PERMISSIONS.identitySingleKeyIssue));
  const canApproveQr = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityQrApprove));

  const items = [
    canReadUsers ? { key: 'users', label: '用户', children: <UsersPanel /> } : null,
    canReadRoles ? { key: 'roles', label: '角色', children: <RolesPanel /> } : null,
    canIssueKey ? { key: 'singlekey', label: '单次密钥', children: <SingleLoginKeyPanel /> } : null,
    canApproveQr ? { key: 'qr', label: '扫码审批', children: <QrApprovalPanel /> } : null,
    canReadSessions ? { key: 'sessions', label: '会话', children: <SessionsPanel /> } : null,
  ].filter((item) => item !== null);

  return (
    <Card title="身份与权限" size="small">
      {items.length > 0 ? (
        <Tabs items={items} />
      ) : (
        <Alert
          type="warning"
          showIcon
          message="当前账号没有身份管理相关权限"
          description="需要 identity.user.read、identity.role.read、identity.session.read、identity.singlekey.issue 或 identity.qr.approve 之一。"
        />
      )}
    </Card>
  );
}
