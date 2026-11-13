import { ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Badge,
  Button,
  Card,
  Descriptions,
  Empty,
  Grid,
  Modal,
  Pagination,
  Popconfirm,
  Select,
  Space,
  Spin,
  Table,
  Tag,
  Typography,
  theme,
} from 'antd';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { PageHeading } from '../../app/PageHeading';
import { identityApi, type QrSessionListQuery } from '../../shared/api/identity';
import type { QrSessionSummary } from '../../shared/api/types';
import { DurationPicker } from './DurationPicker';
import {
  DEFAULT_DURATION_MINUTES,
  formatCountdown,
  formatDateTime,
  formatDuration,
  qrStatusMeta,
  remainingSeconds,
  useTicker,
} from './singleLoginUi';

const DEFAULT_PAGE_SIZE = 20;
const PENDING_REFETCH_MS = 5000;

const STATUS_OPTIONS = [
  { value: 'pending', label: '待审批' },
  { value: 'approved', label: '已批准' },
  { value: 'rejected', label: '已拒绝' },
  { value: 'expired', label: '已过期' },
  { value: '', label: '全部' },
];

/** 到期时间：待审批的挂上倒计时，其余只报时刻。 */
function expiryText(session: QrSessionSummary, now: number): string {
  const at = formatDateTime(session.expiresAt);
  if (session.status !== 'pending') {
    return at;
  }
  const left = remainingSeconds(session.expiresAt, now);
  return left === null ? `${at}（已过期）` : `${at}（剩余 ${formatCountdown(left)}）`;
}

function fingerprintNode(value?: string | null) {
  return value ? (
    <Typography.Text code copyable={{ text: value }} className="qr-mono">
      {value}
    </Typography.Text>
  ) : (
    <Typography.Text type="secondary">无</Typography.Text>
  );
}

/**
 * 扫码登录审批列表。
 *
 * 手机上不渲染表格：七列挤在 390px 里只能横着拖，现场戴着手套根本点不中。
 * 窄屏改成卡片 + 整宽按钮，宽屏保留表格便于批量核对。
 * SignalR 只是提示，待审批列表仍按固定间隔回拉，避免漏掉推送。
 */
export function QrApprovalPage() {
  const { message } = App.useApp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const screens = Grid.useBreakpoint();
  const isMobile = !screens.md;
  const { token } = theme.useToken();

  const [status, setStatus] = useState('pending');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [approving, setApproving] = useState<QrSessionSummary | null>(null);
  const [duration, setDuration] = useState<number | null>(DEFAULT_DURATION_MINUTES);

  const query: QrSessionListQuery = { page, pageSize, status: status || undefined };

  const sessions = useQuery({
    queryKey: ['identity', 'qr-sessions', query],
    queryFn: ({ signal }) => identityApi.listQrSessions(query, signal),
    refetchInterval: status === 'pending' ? PENDING_REFETCH_MS : false,
  });

  const items = sessions.data?.items ?? [];
  const now = useTicker(items.some((item) => item.status === 'pending'));

  const invalidate = async () => {
    await queryClient.invalidateQueries({ queryKey: ['identity', 'qr-sessions'] });
    await queryClient.invalidateQueries({ queryKey: ['identity', 'qr-session'] });
  };

  const approveMutation = useMutation({
    mutationFn: (target: QrSessionSummary) =>
      identityApi.approveQrSession(target.requestId, { durationMinutes: duration ?? undefined }),
    onSuccess: async (_result, target) => {
      await invalidate();
      setApproving(null);
      message.success(`已批准 ${target.clientName ?? '该客户端'}，客户端可领取临时令牌`);
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

  const openApprove = (session: QrSessionSummary) => {
    setDuration(DEFAULT_DURATION_MINUTES);
    setApproving(session);
  };

  const columns = [
    {
      title: '客户端',
      key: 'client',
      width: 240,
      render: (_: unknown, session: QrSessionSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{session.clientName ?? '未知客户端'}</Typography.Text>
          {fingerprintNode(session.deviceFingerprint)}
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
    {
      title: '发起时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      render: (value: string) => formatDateTime(value),
    },
    {
      title: '到期时间',
      key: 'expiresAt',
      width: 260,
      render: (_: unknown, session: QrSessionSummary) => expiryText(session, now),
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (value: string) => {
        const meta = qrStatusMeta(value);
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
      width: 210,
      render: (_: unknown, session: QrSessionSummary) => (
        <Space size="small">
          {session.status === 'pending' ? (
            <>
              <Button type="link" size="small" onClick={() => openApprove(session)}>
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
            </>
          ) : null}
          <Button
            type="link"
            size="small"
            onClick={() => navigate(`/qr-login/${session.requestId}`)}
          >
            详情
          </Button>
        </Space>
      ),
    },
  ];

  const pagination = (
    <Pagination
      align="end"
      simple={isMobile}
      current={sessions.data?.page ?? page}
      pageSize={sessions.data?.pageSize ?? pageSize}
      total={sessions.data?.total ?? 0}
      showSizeChanger={!isMobile}
      showTotal={isMobile ? undefined : (total) => `共 ${total} 个扫码会话`}
      onChange={(nextPage, nextPageSize) => {
        setPage(nextPageSize === pageSize ? nextPage : 1);
        setPageSize(nextPageSize);
      }}
    />
  );

  return (
    <div className="field-page field-page--wide">
      <PageHeading
        title="扫码登录"
        description="客户端出示二维码 → 手机相机扫开审批页 → 核对设备与来源后批准。"
        extra={
          status === 'pending' ? (
            <Badge
              count={sessions.data?.total ?? 0}
              showZero
              color={token.colorWarning}
              overflowCount={99}
            />
          ) : null
        }
      />

      <Card size="small">
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Space wrap size="small">
            <Select
              value={status}
              options={STATUS_OPTIONS}
              style={{ width: 132 }}
              onChange={(value) => {
                setStatus(value);
                setPage(1);
              }}
              aria-label="按状态筛选"
            />
            <Button
              icon={<ReloadOutlined />}
              loading={sessions.isFetching}
              onClick={() => void sessions.refetch()}
            >
              刷新
            </Button>
            {status === 'pending' ? (
              <Typography.Text type="secondary">
                每 {PENDING_REFETCH_MS / 1000} 秒自动刷新
              </Typography.Text>
            ) : null}
          </Space>

          {sessions.isError ? (
            <Alert type="error" showIcon message={(sessions.error as Error).message} />
          ) : null}

          {sessions.isPending ? (
            <div className="field-page__loading">
              <Spin />
            </div>
          ) : null}

          {!sessions.isPending && items.length === 0 ? (
            <Empty
              description={
                status === 'pending'
                  ? '当前没有待审批的扫码请求。让现场在客户端点「扫码登录」，这里几秒内就会出现。'
                  : '没有匹配的扫码会话'
              }
            />
          ) : null}

          {items.length > 0 && isMobile ? (
            <div className="qr-cards">
              {items.map((session) => {
                const meta = qrStatusMeta(session.status);
                return (
                  <Card
                    key={session.requestId}
                    size="small"
                    className="qr-card"
                    title={session.clientName ?? '未知客户端'}
                    extra={<Tag color={meta.color}>{meta.label}</Tag>}
                  >
                    <Space direction="vertical" size="small" style={{ width: '100%' }}>
                      <Descriptions
                        size="small"
                        column={1}
                        items={[
                          {
                            key: 'device',
                            label: '设备指纹',
                            children: fingerprintNode(session.deviceFingerprint),
                          },
                          {
                            key: 'ip',
                            label: '来源 IP',
                            children: session.createdByIp ?? '未知',
                          },
                          {
                            key: 'createdAt',
                            label: '发起时间',
                            children: formatDateTime(session.createdAt),
                          },
                          {
                            key: 'expiresAt',
                            label: '到期时间',
                            children: expiryText(session, now),
                          },
                          ...(session.status === 'pending'
                            ? []
                            : [
                                {
                                  key: 'approvedBy',
                                  label: '审批人',
                                  children: session.approvedByName ?? '-',
                                },
                              ]),
                        ]}
                      />

                      {session.status === 'pending' ? (
                        <div className="qr-card__actions">
                          <Button type="primary" size="large" block onClick={() => openApprove(session)}>
                            批准
                          </Button>
                          <Popconfirm
                            title="拒绝该扫码登录？"
                            description="客户端将收到拒绝状态，无法获得临时令牌。"
                            okText="拒绝"
                            okButtonProps={{ danger: true }}
                            cancelText="取消"
                            onConfirm={() =>
                              rejectMutation.mutateAsync(session.requestId).catch(() => undefined)
                            }
                          >
                            <Button danger size="large" block loading={rejectMutation.isPending}>
                              拒绝
                            </Button>
                          </Popconfirm>
                        </div>
                      ) : (
                        <Button block onClick={() => navigate(`/qr-login/${session.requestId}`)}>
                          查看详情
                        </Button>
                      )}
                    </Space>
                  </Card>
                );
              })}
            </div>
          ) : null}

          {items.length > 0 && !isMobile ? (
            <Table<QrSessionSummary>
              rowKey="requestId"
              size="small"
              dataSource={items}
              columns={columns}
              pagination={false}
              scroll={{ x: 'max-content' }}
            />
          ) : null}

          {(sessions.data?.total ?? 0) > 0 ? pagination : null}
        </Space>
      </Card>

      <Modal
        open={approving !== null}
        title="批准扫码登录"
        okText={`批准（${formatDuration(duration ?? DEFAULT_DURATION_MINUTES)}）`}
        cancelText="取消"
        centered={isMobile}
        width={isMobile ? '92vw' : 520}
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
            type="warning"
            showIcon
            message="先核对是不是本人"
            description="批准后客户端立刻能拿到临时令牌，请确认设备指纹与来源 IP 与现场一致。"
          />
          {approving ? (
            <Descriptions
              size="small"
              column={1}
              items={[
                { key: 'client', label: '客户端', children: approving.clientName ?? '未知客户端' },
                {
                  key: 'device',
                  label: '设备指纹',
                  children: fingerprintNode(approving.deviceFingerprint),
                },
                { key: 'ip', label: '来源 IP', children: approving.createdByIp ?? '未知' },
                {
                  key: 'createdAt',
                  label: '发起时间',
                  children: formatDateTime(approving.createdAt),
                },
              ]}
            />
          ) : null}
          <DurationPicker value={duration} onChange={setDuration} label="临时令牌有效期" />
        </Space>
      </Modal>
    </div>
  );
}
