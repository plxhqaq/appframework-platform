import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  InputNumber,
  Modal,
  Popconfirm,
  Select,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import { useState } from 'react';
import { identityApi, type QrSessionListQuery } from '../../shared/api/identity';
import type { QrSessionSummary } from '../../shared/api/types';

const DEFAULT_PAGE_SIZE = 20;
const PENDING_REFETCH_MS = 5000;

const STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: 'pending', label: '待审批' },
  { value: 'approved', label: '已批准' },
  { value: 'rejected', label: '已拒绝' },
  { value: 'expired', label: '已过期' },
  { value: '', label: '全部' },
];

const STATUS_META: Record<string, { color: string; label: string }> = {
  pending: { color: 'gold', label: '待审批' },
  approved: { color: 'green', label: '已批准' },
  rejected: { color: 'red', label: '已拒绝' },
  expired: { color: 'default', label: '已过期' },
};

function formatTime(value?: string | null): string {
  return value ? new Date(value).toLocaleString() : '-';
}

/**
 * 扫码登录审批：客户端展示二维码后在这里批准。
 * SignalR 只是提示，待审批列表仍按固定间隔回拉，避免漏掉推送。
 */
export function QrApprovalPanel() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();

  const [status, setStatus] = useState('pending');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [approving, setApproving] = useState<QrSessionSummary | null>(null);
  const [duration, setDuration] = useState(30);

  const query: QrSessionListQuery = {
    page,
    pageSize,
    status: status || undefined,
  };

  const sessions = useQuery({
    queryKey: ['identity', 'qr-sessions', query],
    queryFn: ({ signal }) => identityApi.listQrSessions(query, signal),
    refetchInterval: status === 'pending' ? PENDING_REFETCH_MS : false,
  });

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: ['identity', 'qr-sessions'] });

  const approveMutation = useMutation({
    mutationFn: (target: QrSessionSummary) =>
      identityApi.approveQrSession(target.requestId, { durationMinutes: duration }),
    onSuccess: async () => {
      await invalidate();
      setApproving(null);
      message.success('已批准，客户端可领取临时令牌');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const rejectMutation = useMutation({
    mutationFn: (requestId: string) => identityApi.rejectQrSession(requestId),
    onSuccess: async () => {
      await invalidate();
      message.success('已拒绝');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const columns = [
    {
      title: '客户端',
      key: 'client',
      width: 220,
      render: (_: unknown, session: QrSessionSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{session.clientName ?? '未知客户端'}</Typography.Text>
          {session.deviceFingerprint ? (
            <Tooltip title={session.deviceFingerprint}>
              <Typography.Text type="secondary" ellipsis style={{ maxWidth: 200 }}>
                {session.deviceFingerprint}
              </Typography.Text>
            </Tooltip>
          ) : null}
        </Space>
      ),
    },
    {
      title: '来源 IP',
      dataIndex: 'createdByIp',
      key: 'createdByIp',
      width: 140,
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    { title: '发起时间', dataIndex: 'createdAt', key: 'createdAt', width: 180, render: formatTime },
    { title: '到期时间', dataIndex: 'expiresAt', key: 'expiresAt', width: 180, render: formatTime },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (value: string) => {
        const meta = STATUS_META[value] ?? { color: 'default', label: value };
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: '审批人',
      dataIndex: 'approvedByName',
      key: 'approvedByName',
      width: 140,
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    {
      title: '操作',
      key: 'actions',
      width: 170,
      render: (_: unknown, session: QrSessionSummary) =>
        session.status === 'pending' ? (
          <Space size="small">
            <Button
              type="link"
              size="small"
              onClick={() => {
                setDuration(30);
                setApproving(session);
              }}
            >
              批准
            </Button>
            <Popconfirm
              title="拒绝该扫码登录？"
              description="客户端将收到拒绝状态，无法获得临时令牌。"
              okText="拒绝"
              okButtonProps={{ danger: true }}
              cancelText="取消"
              onConfirm={() => rejectMutation.mutateAsync(session.requestId).catch(() => undefined)}
            >
              <Button type="link" size="small" danger>
                拒绝
              </Button>
            </Popconfirm>
          </Space>
        ) : (
          <Typography.Text type="secondary">-</Typography.Text>
        ),
    },
  ];

  return (
    <Space direction="vertical" size="small" style={{ width: '100%' }}>
      <Space wrap>
        <Select
          value={status}
          options={STATUS_OPTIONS}
          style={{ width: 140 }}
          onChange={(value) => {
            setStatus(value);
            setPage(1);
          }}
        />
        <Typography.Text type="secondary">
          待审批列表每 {PENDING_REFETCH_MS / 1000} 秒自动刷新
        </Typography.Text>
      </Space>

      {sessions.isError ? (
        <Alert type="error" showIcon message={(sessions.error as Error).message} />
      ) : null}

      <Table<QrSessionSummary>
        rowKey="requestId"
        size="small"
        loading={sessions.isPending}
        dataSource={sessions.data?.items ?? []}
        columns={columns}
        pagination={{
          current: sessions.data?.page ?? page,
          pageSize: sessions.data?.pageSize ?? pageSize,
          total: sessions.data?.total ?? 0,
          showSizeChanger: true,
          showTotal: (total) => `共 ${total} 个扫码会话`,
          onChange: (nextPage, nextPageSize) => {
            setPage(nextPageSize === pageSize ? nextPage : 1);
            setPageSize(nextPageSize);
          },
        }}
        locale={{ emptyText: '没有匹配的扫码会话' }}
      />

      <Modal
        open={approving !== null}
        title="批准扫码登录"
        okText="批准"
        cancelText="取消"
        confirmLoading={approveMutation.isPending}
        onCancel={() => setApproving(null)}
        onOk={() => {
          if (approving) {
            approveMutation.mutate(approving);
          }
        }}
      >
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Alert
            type="info"
            showIcon
            message="请确认发起人身份"
            description={
              approving
                ? `${approving.clientName ?? '未知客户端'} · ${approving.deviceFingerprint ?? '无设备指纹'} · ${approving.createdByIp ?? '未知 IP'}`
                : undefined
            }
          />
          <Space>
            <Typography.Text>临时令牌有效期（分钟）</Typography.Text>
            <InputNumber
              min={1}
              max={60}
              precision={0}
              value={duration}
              onChange={(value) => setDuration(value ?? 30)}
            />
          </Space>
        </Space>
      </Modal>
    </Space>
  );
}
