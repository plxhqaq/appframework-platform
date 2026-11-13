import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Popconfirm, Space, Table, Tag, Typography } from 'antd';
import { useState } from 'react';
import { identityApi } from '../../shared/api/identity';
import type { RoleSummary } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { RoleFormModal } from './RoleFormModal';

/** 角色管理：角色数量有限，不分页；内置角色不可删除。 */
export function RolesPanel() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityRoleManage));

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<RoleSummary | null>(null);

  const roles = useQuery({
    queryKey: ['identity', 'roles'],
    queryFn: ({ signal }) => identityApi.listRoles(signal),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => identityApi.deleteRole(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['identity', 'roles'] });
      await queryClient.invalidateQueries({ queryKey: ['identity', 'users'] });
      message.success('角色已删除');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const openCreate = () => {
    setEditing(null);
    setFormOpen(true);
  };

  const columns = [
    {
      title: '角色名',
      dataIndex: 'name',
      key: 'name',
      width: 160,
      render: (name: string, role: RoleSummary) => (
        <Space size={4}>
          <Typography.Text code>{name}</Typography.Text>
          {role.isSystem ? <Tag color="blue">内置</Tag> : null}
        </Space>
      ),
    },
    {
      title: '显示名',
      dataIndex: 'displayName',
      key: 'displayName',
      width: 150,
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: '作用域',
      dataIndex: 'scope',
      key: 'scope',
      width: 160,
      render: (scope: string, role: RoleSummary) =>
        scope === 'app' ? (
          <Tag color="purple">app · {role.appKey ?? '-'}</Tag>
        ) : (
          <Tag color="geekblue">platform</Tag>
        ),
    },
    {
      title: '权限',
      dataIndex: 'permissions',
      key: 'permissions',
      render: (permissions: string[]) =>
        permissions.includes('*') ? (
          <Tag color="gold">*（全部权限）</Tag>
        ) : permissions.length > 0 ? (
          <Space size={[4, 4]} wrap>
            {permissions.slice(0, 6).map((code) => (
              <Tag key={code}>{code}</Tag>
            ))}
            {permissions.length > 6 ? <Tag>等 {permissions.length} 项</Tag> : null}
          </Space>
        ) : (
          <Typography.Text type="secondary">无</Typography.Text>
        ),
    },
    {
      title: '用户数',
      dataIndex: 'userCount',
      key: 'userCount',
      width: 80,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      render: (value: string) => new Date(value).toLocaleString(),
    },
    {
      title: '操作',
      key: 'actions',
      width: 140,
      render: (_: unknown, role: RoleSummary) => (
        <Space size="small">
          {canManage ? (
            <Button
              type="link"
              size="small"
              onClick={() => {
                setEditing(role);
                setFormOpen(true);
              }}
            >
              编辑
            </Button>
          ) : null}
          {canManage ? (
            <Popconfirm
              title={`删除角色 ${role.name}？`}
              description={role.isSystem ? '内置角色不可删除。' : '仍有用户使用该角色时不可删除。'}
              okText="删除"
              okButtonProps={{ danger: true }}
              cancelText="取消"
              disabled={role.isSystem}
              onConfirm={() => deleteMutation.mutateAsync(role.id).catch(() => undefined)}
            >
              <Button type="link" size="small" danger disabled={role.isSystem}>
                删除
              </Button>
            </Popconfirm>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size="small" style={{ width: '100%' }}>
      <Space style={{ width: '100%', justifyContent: 'flex-end' }}>
        {canManage ? (
          <Button type="primary" onClick={openCreate}>
            新建角色
          </Button>
        ) : null}
      </Space>

      {roles.isError ? (
        <Alert type="error" showIcon message={(roles.error as Error).message} />
      ) : null}

      <Table<RoleSummary>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={roles.isPending}
        dataSource={roles.data ?? []}
        columns={columns}
        pagination={false}
        locale={{ emptyText: '尚未定义角色' }}
      />

      <RoleFormModal
        open={formOpen}
        role={editing}
        onClose={() => {
          setFormOpen(false);
          setEditing(null);
        }}
      />
    </Space>
  );
}
