import { ArrowLeftOutlined, CheckOutlined, CloseOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Descriptions,
  Popconfirm,
  Result,
  Skeleton,
  Space,
  Tag,
  Typography,
} from 'antd';
import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { PageHeading } from '../../app/PageHeading';
import { ApiError } from '../../shared/api/client';
import { identityApi } from '../../shared/api/identity';
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

const PENDING_REFETCH_MS = 5000;

/**
 * 手机扫码落点：`/qr-login/{请求id}`。
 *
 * 二维码里就是这个页面的 https 链接，相机扫开直达这一单，不用先在列表里找。
 * 未登录会先跳登录页并带上 returnTo，登录完回到这里；
 * 临时会话（密钥/扫码换来的令牌）进不来，路由守卫会给出解释。
 */
export function QrSessionDetailPage() {
  const { requestId = '' } = useParams();
  const navigate = useNavigate();
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [duration, setDuration] = useState<number | null>(DEFAULT_DURATION_MINUTES);

  const sessionQuery = useQuery({
    queryKey: ['identity', 'qr-session', requestId],
    queryFn: ({ signal }) => identityApi.getQrSession(requestId, signal),
    refetchInterval: (query) =>
      query.state.data?.status === 'pending' ? PENDING_REFETCH_MS : false,
    retry: false,
  });

  const current = sessionQuery.data;
  const now = useTicker(current?.status === 'pending');
  const left = current ? remainingSeconds(current.expiresAt, now) : null;
  // 状态还没回拉到、但本地时钟已经过点：先按过期处理，别让现场批一张废票
  const stale = current?.status === 'pending' && left === null;
  const actionable = current?.status === 'pending' && !stale;

  const settle = async (result: QrSessionSummary) => {
    queryClient.setQueryData(['identity', 'qr-session', requestId], result);
    await queryClient.invalidateQueries({ queryKey: ['identity', 'qr-sessions'] });
  };

  const approveMutation = useMutation({
    mutationFn: () =>
      identityApi.approveQrSession(requestId, { durationMinutes: duration ?? undefined }),
    onSuccess: async (result) => {
      await settle(result);
      message.success('已批准，客户端可领取临时令牌');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const rejectMutation = useMutation({
    mutationFn: () => identityApi.rejectQrSession(requestId),
    onSuccess: async (result) => {
      await settle(result);
      message.success('已拒绝');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const backToList = (
    <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/qr-approval')}>
      返回扫码列表
    </Button>
  );

  if (sessionQuery.isPending) {
    return (
      <div className="field-page">
        <PageHeading title="扫码登录审批" />
        <Card size="small">
          <Skeleton active paragraph={{ rows: 5 }} />
        </Card>
      </div>
    );
  }

  if (sessionQuery.isError) {
    const error = sessionQuery.error;
    const notFound = error instanceof ApiError && error.status === 404;
    return (
      <div className="field-page">
        <Result
          status={notFound ? '404' : 'warning'}
          title={notFound ? '这个扫码请求不存在' : '取不到扫码请求'}
          subTitle={
            notFound
              ? '二维码可能已被清理，或者链接被截断了。让现场在客户端重新点「扫码登录」生成一张新的。'
              : error.message
          }
          extra={
            <Space wrap>
              {notFound ? null : (
                <Button type="primary" onClick={() => void sessionQuery.refetch()}>
                  重试
                </Button>
              )}
              {backToList}
            </Space>
          }
        />
      </div>
    );
  }

  const session = sessionQuery.data;
  const meta = qrStatusMeta(session.status);

  return (
    <div className="field-page">
      <PageHeading
        title="扫码登录审批"
        description="核对设备与来源，确认是本人再批准。"
        extra={actionable ? <Tag color={meta.color}>{meta.label}</Tag> : null}
      />

      {session.status === 'approved' ? (
        <Alert
          type="success"
          showIcon
          message="已批准"
          description={`${session.approvedByName ?? '管理员'} 于 ${formatDateTime(session.approvedAt)} 批准，客户端已可领取临时令牌。`}
        />
      ) : null}
      {session.status === 'rejected' ? (
        <Alert
          type="error"
          showIcon
          message="已拒绝"
          description="客户端拿不到临时令牌。需要放行的话，让现场重新发起一次扫码。"
        />
      ) : null}
      {session.status === 'expired' || stale ? (
        <Alert
          type="warning"
          showIcon
          message="这一单已过期"
          description="二维码有效期只有 10 分钟。让现场在客户端重新点「扫码登录」生成一张新的。"
        />
      ) : null}

      <Card size="small" title={session.clientName ?? '未知客户端'}>
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Descriptions
            size="small"
            column={1}
            items={[
              {
                key: 'device',
                label: '设备指纹',
                children: session.deviceFingerprint ? (
                  <Typography.Text code copyable={{ text: session.deviceFingerprint }} className="qr-mono">
                    {session.deviceFingerprint}
                  </Typography.Text>
                ) : (
                  <Typography.Text type="secondary">无</Typography.Text>
                ),
              },
              { key: 'ip', label: '来源 IP', children: session.createdByIp ?? '未知' },
              {
                key: 'createdAt',
                label: '发起时间',
                children: formatDateTime(session.createdAt),
              },
              {
                key: 'expiresAt',
                label: '到期时间',
                children: (
                  <Space size="small" wrap>
                    <span>{formatDateTime(session.expiresAt)}</span>
                    {session.status === 'pending' && left !== null ? (
                      <Tag color="processing">剩余 {formatCountdown(left)}</Tag>
                    ) : null}
                  </Space>
                ),
              },
              { key: 'requestId', label: '请求 ID', children: <Typography.Text code className="qr-mono">{session.requestId}</Typography.Text> },
            ]}
          />

          {actionable ? (
            <>
              <DurationPicker value={duration} onChange={setDuration} label="临时令牌有效期" />
              <div className="qr-card__actions">
                <Button
                  type="primary"
                  size="large"
                  block
                  icon={<CheckOutlined />}
                  loading={approveMutation.isPending}
                  onClick={() => approveMutation.mutate()}
                >
                  批准（{formatDuration(duration ?? DEFAULT_DURATION_MINUTES)}）
                </Button>
                <Popconfirm
                  title="拒绝该扫码登录？"
                  description="客户端将收到拒绝状态，无法获得临时令牌。"
                  okText="拒绝"
                  okButtonProps={{ danger: true }}
                  cancelText="取消"
                  onConfirm={() => rejectMutation.mutateAsync().catch(() => undefined)}
                >
                  <Button danger size="large" block icon={<CloseOutlined />} loading={rejectMutation.isPending}>
                    拒绝
                  </Button>
                </Popconfirm>
              </div>
            </>
          ) : null}

          {backToList}
        </Space>
      </Card>
    </div>
  );
}
