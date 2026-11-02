import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Alert, Button, Card, Select, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { useState } from 'react';
import { webhookApi } from '../../shared/api/webhooks';
import { canRedeliver, DELIVERY_STATUS_META, formatPayload } from './webhookUi';
import type { WebhookDelivery, WebhookDeliveryStatus, WebhookSubscription } from '../../shared/api/types';

interface DeliveriesPanelProps {
  subscriptions: WebhookSubscription[];
  canManage: boolean;
  /** 当前应用；为空时不发起请求。 */
  appKey: string | null;
}

/** 投递台账：按订阅过滤、查看负载与错误、手动重投。 */
export function DeliveriesPanel({ subscriptions, canManage, appKey }: DeliveriesPanelProps) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [subscriptionId, setSubscriptionId] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  const urlById = new Map(subscriptions.map((item) => [item.id, item.url]));

  const deliveries = useQuery({
    queryKey: ['webhooks', 'deliveries', appKey, subscriptionId, page, pageSize],
    queryFn: ({ signal }) =>
      webhookApi.listDeliveries({ subscriptionId, page, pageSize }, signal),
    enabled: appKey !== null,
  });

  const redeliver = useMutation({
    mutationFn: (id: string) => webhookApi.redeliver(id),
    onSuccess: (delivery) => {
      message.success(`投递 ${delivery.id.slice(0, 8)} 已重新入队（第 ${delivery.attempts + 1} 次尝试待执行）`);
      void queryClient.invalidateQueries({ queryKey: ['webhooks', 'deliveries'] });
      void queryClient.invalidateQueries({ queryKey: ['webhooks', 'subscriptions'] });
    },
    onError: (error: Error) => message.error(error.message),
  });

  return (
    <Card
      size="small"
      title="投递台账"
      extra={
        <Space wrap>
          <Select
            allowClear
            size="small"
            style={{ minWidth: 260 }}
            placeholder="按订阅过滤"
            value={subscriptionId}
            onChange={(value) => {
              setSubscriptionId(value);
              setPage(1);
            }}
            options={subscriptions.map((item) => ({ value: item.id, label: item.url }))}
          />
          <Button size="small" loading={deliveries.isFetching} onClick={() => void deliveries.refetch()}>
            刷新
          </Button>
          <Typography.Text type="secondary">共 {deliveries.data?.total ?? 0} 条</Typography.Text>
        </Space>
      }
    >
      {deliveries.isError ? (
        <Alert type="error" showIcon style={{ marginBottom: 12 }} message={(deliveries.error as Error).message} />
      ) : null}
      <Table<WebhookDelivery>
        rowKey="id"
        size="small"
        loading={deliveries.isPending}
        dataSource={deliveries.data?.items ?? []}
        scroll={{ x: 'max-content' }}
        pagination={{
          current: page,
          pageSize,
          total: deliveries.data?.total ?? 0,
          showSizeChanger: true,
          onChange: (nextPage, nextPageSize) => {
            setPage(nextPage);
            setPageSize(nextPageSize);
          },
        }}
        expandable={{
          expandedRowRender: (record) => (
            <Space direction="vertical" size={4} style={{ width: '100%' }}>
              <Typography.Text type="secondary">
                订阅：{urlById.get(record.subscriptionId) ?? record.subscriptionId} /
                Delivery：<Typography.Text code>{record.id}</Typography.Text>
              </Typography.Text>
              {record.lastError ? (
                <Typography.Text type="danger">最近错误：{record.lastError}</Typography.Text>
              ) : null}
              <pre style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{formatPayload(record.payloadJson)}</pre>
            </Space>
          ),
        }}
        columns={[
          {
            title: '生成时间',
            dataIndex: 'createdAt',
            width: 170,
            render: (value: string) => new Date(value).toLocaleString(),
          },
          {
            title: '事件',
            dataIndex: 'eventType',
            width: 160,
            render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
          },
          {
            title: '状态',
            dataIndex: 'status',
            width: 120,
            render: (value: WebhookDeliveryStatus) => (
              <Tag color={DELIVERY_STATUS_META[value]?.color ?? 'default'}>{DELIVERY_STATUS_META[value]?.label ?? value}</Tag>
            ),
          },
          { title: '尝试', dataIndex: 'attempts', width: 70 },
          {
            title: '状态码',
            dataIndex: 'lastStatusCode',
            width: 90,
            render: (value: number | null) => value ?? '-',
          },
          {
            title: '下次尝试',
            dataIndex: 'nextAttemptAt',
            width: 170,
            render: (value: string | null, record) =>
              record.status === 'pending' || record.status === 'failed'
                ? value
                  ? new Date(value).toLocaleString()
                  : '-'
                : '-',
          },
          {
            title: '错误',
            dataIndex: 'lastError',
            ellipsis: true,
            render: (value: string | null) => value ?? '-',
          },
          {
            title: '操作',
            key: 'actions',
            width: 90,
            render: (_: unknown, record) => {
              if (!canManage || !canRedeliver(record.status)) {
                return null;
              }

              return (
                <Tooltip title="重新入队，退避计时从头开始">
                  <Button
                    type="link"
                    size="small"
                    loading={redeliver.isPending && redeliver.variables === record.id}
                    onClick={() => redeliver.mutate(record.id)}
                  >
                    重投
                  </Button>
                </Tooltip>
              );
            },
          },
        ]}
      />
    </Card>
  );
}
