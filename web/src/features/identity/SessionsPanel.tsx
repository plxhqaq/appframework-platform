import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Input, Popconfirm, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { useState } from 'react';
import { identityApi } from '../../shared/api/identity';
import type { SessionSummary } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';

const DEFAULT_PAGE_SIZE = 20;

function formatTime(value?: string | null): string {
  return value ? new Date(value).toLocaleString() : '-';
}

/** 会话 = 一条刷新令牌家族记录；吊销后该设备需要重新登录。 */
export function SessionsPanel() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const canRevoke = useAuthStore((state) => state.hasPermission(PERMISSIONS.identitySessionRevoke));

  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [userId, setUserId] = useState('');

  const sessions = useQuery({
    queryKey: ['identity', 'sessions', page, pageSize, userId],
    queryFn: ({ signal }) =>
      identityApi.listSessions({ page, pageSize, userId: userId || undefined }, signal),
  });

  const revokeMutation = useMutation({
    mutationFn: (id: string) => identityApi.revokeSession(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['identity', 'sessions'] });
      message.success('会话已吊销');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const columns = [
    {
      title: '用户',
      dataIndex: 'userName',
      key: 'userName',
      width: 150,
      render: (userName: string) => <Typography.Text code>{userName}</Typography.Text>,
    },
    {
      title: '设备指纹',
      dataIndex: 'deviceFingerprint',
      key: 'deviceFingerprint',
      width: 180,
      render: (value: string | null) =>
        value ? (
          <Tooltip title={value}>
            <Typography.Text ellipsis style={{ maxWidth: 160 }}>
              {value}
            </Typography.Text>
          </Tooltip>
        ) : (
          <Typography.Text type="secondary">-</Typography.Text>
        ),
    },
    {
      title: '来源 IP',
      dataIndex: 'createdByIp',
      key: 'createdByIp',
      width: 140,
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    { title: '创建时间', dataIndex: 'createdAt', key: 'createdAt', width: 180, render: formatTime },
    { title: '最后使用', dataIndex: 'lastUsedAt', key: 'lastUsedAt', width: 180, render: formatTime },
    { title: '过期时间', dataIndex: 'expiresAt', key: 'expiresAt', width: 180, render: formatTime },
    {
      title: '状态',
      dataIndex: 'isActive',
      key: 'isActive',
      width: 90,
      render: (active: boolean) => (active ? <Tag color="green">有效</Tag> : <Tag>已失效</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 90,
      render: (_: unknown, session: SessionSummary) =>
        canRevoke && session.isActive ? (
          <Popconfirm
            title="吊销该会话？"
            description="该设备上的刷新令牌会立即失效，需要重新登录。"
            okText="吊销"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => revokeMutation.mutateAsync(session.id).catch(() => undefined)}
          >
            <Button type="link" size="small" danger>
              吊销
            </Button>
          </Popconfirm>
        ) : (
          <Typography.Text type="secondary">-</Typography.Text>
        ),
    },
  ];

  return (
    <Space direction="vertical" size="small" style={{ width: '100%' }}>
      <Input.Search
        allowClear
        placeholder="按用户 ID 过滤"
        style={{ width: 320 }}
        loading={sessions.isFetching}
        onSearch={(value) => {
          setUserId(value.trim());
          setPage(1);
        }}
      />

      {sessions.isError ? (
        <Alert type="error" showIcon message={(sessions.error as Error).message} />
      ) : null}

      <Table<SessionSummary>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={sessions.isPending}
        dataSource={sessions.data?.items ?? []}
        columns={columns}
        pagination={{
          current: sessions.data?.page ?? page,
          pageSize: sessions.data?.pageSize ?? pageSize,
          total: sessions.data?.total ?? 0,
          showSizeChanger: true,
          showTotal: (total) => `共 ${total} 个会话`,
          onChange: (nextPage, nextPageSize) => {
            setPage(nextPageSize === pageSize ? nextPage : 1);
            setPageSize(nextPageSize);
          },
        }}
        locale={{ emptyText: '没有匹配的会话' }}
      />
    </Space>
  );
}
