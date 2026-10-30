import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Input, Popconfirm, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { useState } from 'react';
import { identityApi } from '../../shared/api/identity';
import type { UserDetail, UserSummary } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { ResetPasswordModal } from './ResetPasswordModal';
import { UserFormModal } from './UserFormModal';

const DEFAULT_PAGE_SIZE = 20;

function formatTime(value?: string | null): string {
  return value ? new Date(value).toLocaleString() : '-';
}

/** 用户管理：分页 + 关键字；口令只能通过"重置"动作修改，避免管理员看到旧口令。 */
export function UsersPanel() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityUserManage));
  const canReset = useAuthStore((state) => state.hasPermission(PERMISSIONS.identityUserPasswordReset));

  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [keyword, setKeyword] = useState('');
  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<UserDetail | null>(null);
  const [resetting, setResetting] = useState<UserSummary | null>(null);
  const [loadingDetailId, setLoadingDetailId] = useState<string | null>(null);

  const users = useQuery({
    queryKey: ['identity', 'users', page, pageSize, keyword],
    queryFn: ({ signal }) =>
      identityApi.listUsers({ page, pageSize, keyword: keyword || undefined }, signal),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => identityApi.deleteUser(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['identity', 'users'] });
      await queryClient.invalidateQueries({ queryKey: ['identity', 'roles'] });
      message.success('用户已删除');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const openCreate = () => {
    setEditing(null);
    setFormOpen(true);
  };

  const openEdit = async (user: UserSummary) => {
    setLoadingDetailId(user.id);
    try {
      setEditing(await identityApi.getUser(user.id));
      setFormOpen(true);
    } catch (error) {
      message.error(error instanceof Error ? error.message : '获取用户详情失败');
    } finally {
      setLoadingDetailId(null);
    }
  };

  const columns = [
    {
      title: '用户名',
      dataIndex: 'userName',
      key: 'userName',
      width: 150,
      render: (userName: string) => <Typography.Text code>{userName}</Typography.Text>,
    },
    {
      title: '显示名',
      dataIndex: 'displayName',
      key: 'displayName',
      width: 150,
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: '角色',
      dataIndex: 'roles',
      key: 'roles',
      render: (roles: UserSummary['roles']) =>
        roles.length > 0 ? (
          <Space size={[4, 4]} wrap>
            {roles.map((role) => (
              <Tag key={role.id}>{role.displayName ?? role.name}</Tag>
            ))}
          </Space>
        ) : (
          <Typography.Text type="secondary">-</Typography.Text>
        ),
    },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 90,
      render: (enabled: boolean) => (enabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>),
    },
    {
      title: '首登改密',
      dataIndex: 'mustChangePassword',
      key: 'mustChangePassword',
      width: 100,
      render: (required: boolean) =>
        required ? <Tag color="orange">待修改</Tag> : <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: '最近登录',
      dataIndex: 'lastLoginAt',
      key: 'lastLoginAt',
      width: 180,
      render: formatTime,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      render: formatTime,
    },
    {
      title: '操作',
      key: 'actions',
      width: 220,
      render: (_: unknown, user: UserSummary) => (
        <Space size="small" wrap>
          {canManage ? (
            <Tooltip title={user.userName === 'admin' ? '内置 admin 账号仍可编辑资料，但不可删除' : undefined}>
              <Button
                type="link"
                size="small"
                loading={loadingDetailId === user.id}
                onClick={() => void openEdit(user)}
              >
                编辑
              </Button>
            </Tooltip>
          ) : null}
          {canReset ? (
            <Button type="link" size="small" onClick={() => setResetting(user)}>
              重置口令
            </Button>
          ) : null}
          {canManage ? (
            <Popconfirm
              title={`删除用户 ${user.userName}？`}
              description="删除后该用户的历史审计仍保留，但账号不可恢复。"
              okText="删除"
              okButtonProps={{ danger: true }}
              cancelText="取消"
              onConfirm={() => deleteMutation.mutateAsync(user.id).catch(() => undefined)}
            >
              <Button type="link" size="small" danger>
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
      <Space style={{ width: '100%', justifyContent: 'space-between' }}>
        <Input.Search
          allowClear
          placeholder="按用户名或显示名搜索"
          style={{ width: 280 }}
          loading={users.isFetching}
          onSearch={(value) => {
            setKeyword(value.trim());
            setPage(1);
          }}
        />
        {canManage ? (
          <Button type="primary" onClick={openCreate}>
            新建用户
          </Button>
        ) : null}
      </Space>

      {users.isError ? (
        <Alert type="error" showIcon message={(users.error as Error).message} />
      ) : null}

      <Table<UserSummary>
        rowKey="id"
        size="small"
        loading={users.isPending}
        dataSource={users.data?.items ?? []}
        columns={columns}
        pagination={{
          current: users.data?.page ?? page,
          pageSize: users.data?.pageSize ?? pageSize,
          total: users.data?.total ?? 0,
          showSizeChanger: true,
          showTotal: (total) => `共 ${total} 个用户`,
          onChange: (nextPage, nextPageSize) => {
            setPage(nextPageSize === pageSize ? nextPage : 1);
            setPageSize(nextPageSize);
          },
        }}
        locale={{ emptyText: '没有匹配的用户' }}
      />

      <UserFormModal
        open={formOpen}
        user={editing}
        onClose={() => {
          setFormOpen(false);
          setEditing(null);
        }}
      />
      <ResetPasswordModal open={resetting !== null} user={resetting} onClose={() => setResetting(null)} />
    </Space>
  );
}
