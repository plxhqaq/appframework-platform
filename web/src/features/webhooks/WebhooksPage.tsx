import { PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Popconfirm,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { webhookApi } from '../../shared/api/webhooks';
import type { WebhookSubscription } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { DeliveriesPanel } from './DeliveriesPanel';
import { SecretOnceModal } from './SecretOnceModal';
import { SubscriptionFormModal } from './SubscriptionFormModal';

interface SecretReveal {
  secret: string;
  caption: string;
}

/**
 * Webhook 订阅与投递台账。
 *
 * 订阅按应用隔离，应用上下文来自令牌（与版本/授权页同一口径）。
 * 密钥只在创建与轮换的响应里出现一次，页面不做任何缓存：关掉弹窗就只剩提示。
 */
export function WebhooksPage() {
  const { message, modal } = App.useApp();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const canRead = useAuthStore((state) => state.hasPermission(PERMISSIONS.webhookRead));
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.webhookManage));

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<WebhookSubscription | null>(null);
  const [reveal, setReveal] = useState<SecretReveal | null>(null);

  const subscriptions = useQuery({
    queryKey: ['webhooks', 'subscriptions', appKey],
    queryFn: ({ signal }) => webhookApi.listSubscriptions(signal),
    enabled: canRead && appKey !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['webhooks'] });
  };

  const rotate = useMutation({
    mutationFn: (id: string) => webhookApi.rotateSecret(id),
    onSuccess: (result, id) => {
      const target = subscriptions.data?.find((item) => item.id === id);
      setReveal({ secret: result.secret, caption: `接收地址：${target?.url ?? id}` });
      void refresh();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const remove = useMutation({
    mutationFn: (id: string) => webhookApi.deleteSubscription(id),
    onSuccess: async () => {
      message.success('订阅及其投递记录已删除');
      await refresh();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const sendTest = useMutation({
    mutationFn: (id: string) => webhookApi.sendTest(id),
    onSuccess: (result) => {
      message.success(`已入队 ${result.deliveryCount} 条测试投递，稍后在台账查看结果`);
      void refresh();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleRotate = (row: WebhookSubscription) => {
    modal.confirm({
      title: '轮换签名密钥？',
      content: '旧密钥立即失效：接收方换上新密钥之前，所有投递都会因签名不符被拒。',
      okText: '轮换',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => rotate.mutateAsync(row.id),
    });
  };

  const columns: TableColumnsType<WebhookSubscription> = [
    {
      title: '接收地址',
      dataIndex: 'url',
      key: 'url',
      ellipsis: true,
      render: (url: string) => (
        <Typography.Text code ellipsis={{ tooltip: url }} style={{ maxWidth: 360 }}>
          {url}
        </Typography.Text>
      ),
    },
    {
      title: '订阅事件',
      dataIndex: 'eventTypes',
      key: 'eventTypes',
      width: 260,
      responsive: ['md'],
      render: (eventTypes: string[]) =>
        eventTypes.length === 0 ? (
          <Typography.Text type="secondary">全部事件</Typography.Text>
        ) : (
          <Space size={2} wrap>
            {eventTypes.map((eventType) => (
              <Tag key={eventType}>{eventType}</Tag>
            ))}
          </Space>
        ),
    },
    {
      title: '密钥提示',
      dataIndex: 'secretHint',
      key: 'secretHint',
      width: 190,
      responsive: ['md'],
      render: (hint: string) => <Typography.Text code>{hint}</Typography.Text>,
    },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 90,
      render: (isEnabled: boolean) =>
        isEnabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>,
    },
    {
      title: '最近投递',
      dataIndex: 'lastDeliveryAt',
      key: 'lastDeliveryAt',
      width: 170,
      responsive: ['md'],
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
    {
      title: '创建',
      key: 'created',
      width: 180,
      responsive: ['md'],
      render: (_: unknown, row: WebhookSubscription) => (
        <Space direction="vertical" size={0}>
          <Typography.Text type="secondary">{row.createdBy ?? '-'}</Typography.Text>
          <Typography.Text type="secondary">{new Date(row.createdAt).toLocaleString()}</Typography.Text>
        </Space>
      ),
    },
    {
      title: '操作',
      key: 'actions',
      width: 230,
      fixed: 'right',
      render: (_: unknown, row: WebhookSubscription) => (
        <Space size="small" wrap>
          <Button type="link" size="small" onClick={() => {
            setEditing(row);
            setFormOpen(true);
          }}>
            编辑
          </Button>
          <Tooltip title="投递一条 webhook.test，不看事件白名单">
            <Button
              type="link"
              size="small"
              disabled={!canManage}
              loading={sendTest.isPending && sendTest.variables === row.id}
              onClick={() => sendTest.mutate(row.id)}
            >
              测试
            </Button>
          </Tooltip>
          <Button
            type="link"
            size="small"
            disabled={!canManage}
            onClick={() => handleRotate(row)}
          >
            轮换密钥
          </Button>
          <Popconfirm
            title="删除订阅？"
            description="其投递记录一并删除，且不可恢复。"
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            disabled={!canManage}
            onConfirm={() => remove.mutate(row.id)}
          >
            <Button type="link" size="small" danger disabled={!canManage}>
              删除
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  if (!canRead) {
    return (
      <Alert
        type="warning"
        showIcon
        message="缺少 webhook.read 权限"
        description="Webhook 订阅与投递台账只对持有 webhook.read 的角色开放，请联系平台管理员分配角色。"
      />
    );
  }

  if (appKey === null) {
    return (
      <Card title="Webhook" size="small">
        <Alert
          type="warning"
          showIcon
          message="当前账号没有可用的应用上下文"
          description="订阅按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。"
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title="订阅"
        extra={
          canManage ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => {
                setEditing(null);
                setFormOpen(true);
              }}
            >
              新建订阅
            </Button>
          ) : null
        }
      >
        {subscriptions.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={(subscriptions.error as Error).message}
            action={
              <Button size="small" onClick={() => void subscriptions.refetch()}>
                重试
              </Button>
            }
          />
        ) : null}
        <Table<WebhookSubscription>
          rowKey="id"
          size="small"
          loading={subscriptions.isPending}
          dataSource={subscriptions.data ?? []}
          columns={columns}
          pagination={false}
          scroll={{ x: 'max-content' }}
          locale={{ emptyText: '当前应用还没有 Webhook 订阅' }}
        />
      </Card>

      <DeliveriesPanel
        subscriptions={subscriptions.data ?? []}
        canManage={canManage}
        appKey={appKey}
      />

      <SubscriptionFormModal
        open={formOpen}
        subscription={editing}
        onClose={() => {
          setFormOpen(false);
          setEditing(null);
        }}
        onCreated={(secret, url) => setReveal({ secret, caption: `接收地址：${url}` })}
        onSaved={() => void refresh()}
      />

      <SecretOnceModal
        open={reveal !== null}
        secret={reveal?.secret ?? ''}
        caption={reveal?.caption ?? ''}
        onClose={() => setReveal(null)}
      />
    </Space>
  );
}
