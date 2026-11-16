import { formatDateTime } from '../../shared/format';
import { tr } from '../../i18n/translate';
import { PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Popconfirm, Space, Table, Tag, Tooltip, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { identityApi } from '../../shared/api/identity';
import type { RoleSummary } from '../../shared/api/types';
import { describePermission } from '../../shared/permissionDescriptions';
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
      message.success(tr("角色已删除"));
    },
    onError: (error: Error) => message.error(error.message),
  });

  const openCreate = () => {
    setEditing(null);
    setFormOpen(true);
  };

  const columns: TableColumnsType<RoleSummary> = [
    {
      title: tr("角色名"),
      dataIndex: 'name',
      key: 'name',
      width: 200,
      render: (name: string, role: RoleSummary) => (
        <Space size={4} style={{ whiteSpace: 'nowrap' }}>
          <Typography.Text code>{name}</Typography.Text>
          {role.isSystem ? <Tag color="blue">{tr("内置")}</Tag> : null}
        </Space>
      ),
    },
    {
      title: tr("显示名"),
      dataIndex: 'displayName',
      key: 'displayName',
      width: 150,
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: tr("作用域"),
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
      title: tr("权限"),
      dataIndex: 'permissions',
      key: 'permissions',
      render: (permissions: string[]) =>
        permissions.includes('*') ? (
          <Tooltip title={describePermission('*')}>
            <Tag color="gold">{tr("*（全部权限）")}</Tag>
          </Tooltip>
        ) : permissions.length > 0 ? (
          <Space size={[4, 4]} wrap>
            {permissions.slice(0, 6).map((code) => (
              <Tooltip key={code} title={describePermission(code) ?? code}>
                <Tag>{code}</Tag>
              </Tooltip>
            ))}
            {permissions.length > 6 ? <Tag>{tr("等")}{permissions.length} {tr("项")}</Tag> : null}
          </Space>
        ) : (
          <Typography.Text type="secondary">{tr("无")}</Typography.Text>
        ),
    },
    {
      title: tr("用户数"),
      dataIndex: 'userCount',
      key: 'userCount',
      width: 80,
      responsive: ['md'],
    },
    {
      title: tr("创建时间"),
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      responsive: ['md'],
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 140,
      fixed: 'right',
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
              {tr("编辑")}</Button>
          ) : null}
          {canManage ? (
            <Popconfirm
              title={tr("删除角色 {{arg0}}？", { arg0: role.name })}
              description={role.isSystem ? tr("内置角色不可删除。") : tr("仍有用户使用该角色时不可删除。")}
              okText={tr("删除")}
              okButtonProps={{ danger: true }}
              cancelText={tr("取消")}
              disabled={role.isSystem}
              onConfirm={() => deleteMutation.mutateAsync(role.id).catch(() => undefined)}
            >
              <Button type="link" size="small" danger disabled={role.isSystem}>
                {tr("删除")}</Button>
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
          <Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            {tr("新建角色")}</Button>
        ) : null}
      </Space>

      {roles.isError ? (
        <Alert
          type="error"
          showIcon
          message={(roles.error as Error).message}
          action={
            <Button size="small" onClick={() => void roles.refetch()}>
              {tr("重试")}</Button>
          }
        />
      ) : null}

      <Table<RoleSummary>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={roles.isPending}
        dataSource={roles.data ?? []}
        columns={columns}
        pagination={false}
        locale={{ emptyText: tr("尚未定义角色") }}
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
