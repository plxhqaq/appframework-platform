import { Alert, Card, Descriptions, Space, Spin, Table, Tag, Typography } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { platformApi } from '../../shared/api/platform';
import type { ModuleInfo } from '../../shared/api/types';
import { useAuthStore } from '../../stores/auth';

const columns = [
  { title: '模块键', dataIndex: 'key', key: 'key', width: 140 },
  { title: '显示名键', dataIndex: 'displayNameKey', key: 'displayNameKey', width: 200 },
  {
    title: '阶段',
    dataIndex: 'stage',
    key: 'stage',
    width: 90,
    render: (stage: string) => <Tag color={stage === 'S0' ? 'default' : 'green'}>{stage}</Tag>,
  },
  { title: '版本', dataIndex: 'version', key: 'version', width: 160 },
  {
    title: '路由',
    dataIndex: 'routes',
    key: 'routes',
    render: (routes: string[]) => routes.map((route) => <Tag key={route}>{route}</Tag>),
  },
  {
    title: '声明权限',
    dataIndex: 'requiredPermissions',
    key: 'requiredPermissions',
    render: (codes: string[]) => (
      <Space size={[4, 4]} wrap>
        {codes.map((code) => (
          <Tag key={code} color="blue">
            {code}
          </Tag>
        ))}
      </Space>
    ),
  },
];

/**
 * 总览页：S0 的出口标准就看这一页——
 * 能列出全部模块（证明模块清单与装配可用），能显示当前令牌解析结果（证明 JWT 声明链路可用）。
 */
export function OverviewPage() {
  const session = useAuthStore((state) => state.session);

  const modules = useQuery({
    queryKey: ['platform', 'modules'],
    queryFn: ({ signal }) => platformApi.modules(signal),
  });

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Card title="当前主体" size="small">
        {session ? (
          <Descriptions column={{ xs: 1, sm: 2, md: 3 }} size="small" items={[
            { key: 'userName', label: '用户名', children: session.userName },
            { key: 'userId', label: '用户 ID', children: session.userId },
            { key: 'displayName', label: '显示名', children: session.displayName ?? '-' },
            { key: 'appKeys', label: '可访问应用', children: session.appKeys.join(', ') || '-' },
            { key: 'roles', label: '角色', children: session.roles.join(', ') || '-' },
            {
              key: 'permissions',
              label: '权限',
              children:
                session.permissions.length > 6
                  ? `${session.permissions.slice(0, 6).join(', ')} 等 ${session.permissions.length} 项`
                  : session.permissions.join(', ') || '-',
            },
            { key: 'mustChangePassword', label: '待改密', children: session.mustChangePassword ? '是' : '否' },
          ]} />
        ) : (
          <Typography.Text type="secondary">尚未获取主体信息</Typography.Text>
        )}
      </Card>

      <Card title="平台模块" size="small" extra={<Typography.Text type="secondary">GET /api/v1/platform/modules</Typography.Text>}>
        {modules.isPending ? <Spin /> : null}
        {modules.isError ? (
          <Alert type="error" showIcon message={(modules.error as Error).message} />
        ) : null}
        {modules.data ? (
          <Table<ModuleInfo>
            rowKey="key"
            size="small"
            pagination={false}
            scroll={{ x: 'max-content' }}
            dataSource={modules.data}
            columns={columns}
          />
        ) : null}
      </Card>
    </Space>
  );
}
