import { formatDateTime } from '../../shared/format';
import { tr } from '../../i18n/translate';
import { ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Alert, Button, Card, Select, Space, Table, Tag, Tooltip, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { webhookApi } from '../../shared/api/webhooks';
import { responsivePagination } from '../../shared/responsive';
import { renderTableExpandIcon } from '../../shared/ui/TableExpandIcon';
import { useResponsive } from '../../shared/useResponsive';
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
  const { isMobile } = useResponsive();
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
      message.success(tr("投递 {{arg0}} 已重新入队（第 {{arg1}} 次尝试待执行）", { arg0: delivery.id.slice(0, 8), arg1: delivery.attempts + 1 }));
      void queryClient.invalidateQueries({ queryKey: ['webhooks', 'deliveries'] });
      void queryClient.invalidateQueries({ queryKey: ['webhooks', 'subscriptions'] });
    },
    onError: (error: Error) => message.error(error.message),
  });

  const columns: TableColumnsType<WebhookDelivery> = [
    {
      title: tr("生成时间"),
      dataIndex: 'createdAt',
      width: 170,
      responsive: ['md'],
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("事件"),
      dataIndex: 'eventType',
      width: 160,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: tr("状态"),
      dataIndex: 'status',
      width: 120,
      render: (value: WebhookDeliveryStatus) => (
        <Tag color={DELIVERY_STATUS_META[value]?.color ?? 'default'}>
          {DELIVERY_STATUS_META[value]?.label ?? value}
        </Tag>
      ),
    },
    {
      title: tr("尝试"),
      dataIndex: 'attempts',
      width: 70,
      responsive: ['md'],
    },
    {
      title: tr("状态码"),
      dataIndex: 'lastStatusCode',
      width: 90,
      responsive: ['md'],
      render: (value: number | null) => value ?? '-',
    },
    {
      title: tr("下次尝试"),
      dataIndex: 'nextAttemptAt',
      width: 170,
      responsive: ['md'],
      render: (value: string | null, record) =>
        record.status === 'pending' || record.status === 'failed'
          ? value
            ? formatDateTime(value)
            : '-'
          : '-',
    },
    {
      title: tr("错误"),
      dataIndex: 'lastError',
      ellipsis: { showTitle: false },
      render: (value: string | null) =>
        value ? (
          <Tooltip title={value}>
            <Typography.Text style={{ wordBreak: 'break-all' }}>{value}</Typography.Text>
          </Tooltip>
        ) : (
          '-'
        ),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 90,
      fixed: 'right',
      render: (_: unknown, record) => {
        if (!canManage || !canRedeliver(record.status)) {
          return null;
        }

        return (
          <Tooltip title={tr("重新入队，退避计时从头开始")}>
            <Button
              type="link"
              size="small"
              aria-label={tr("重投该投递")}
              loading={redeliver.isPending && redeliver.variables === record.id}
              onClick={() => redeliver.mutate(record.id)}
            >
              {tr("重投")}</Button>
          </Tooltip>
        );
      },
    },
  ];

  return (
    <Card
      size="small"
      title={tr("投递台账")}
      extra={
        <Space wrap>
          <Select
            allowClear
            size="small"
            style={{ width: 240, maxWidth: '100%', minWidth: 0 }}
            placeholder={tr("按订阅过滤")}
            value={subscriptionId}
            onChange={(value) => {
              setSubscriptionId(value);
              setPage(1);
            }}
            options={subscriptions.map((item) => ({ value: item.id, label: item.url }))}
          />
          <Button
            size="small"
            icon={<ReloadOutlined />}
            loading={deliveries.isFetching}
            aria-label={tr("刷新投递台账")}
            onClick={() => void deliveries.refetch()}
          >
            {tr("刷新")}</Button>
          <Typography.Text type="secondary">{tr("共")}{deliveries.data?.total ?? 0} {tr("条")}</Typography.Text>
        </Space>
      }
    >
      {deliveries.isError ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 12 }}
          message={(deliveries.error as Error).message}
          action={
            <Button size="small" onClick={() => void deliveries.refetch()}>
              {tr("重试")}</Button>
          }
        />
      ) : null}
      <Table<WebhookDelivery>
        rowKey="id"
        size="small"
        loading={deliveries.isPending}
        dataSource={deliveries.data?.items ?? []}
        scroll={{ x: 'max-content' }}
        pagination={responsivePagination(isMobile, {
          current: page,
          pageSize,
          total: deliveries.data?.total ?? 0,
          showTotal: (total) => tr("共 {{arg0}} 条", { arg0: total }),
          onChange: (nextPage: number, nextPageSize: number) => {
            setPage(nextPage);
            setPageSize(nextPageSize);
          },
        })}
        expandable={{
          expandIcon: renderTableExpandIcon,
          expandedRowRender: (record) => (
            <Space direction="vertical" size={4} style={{ width: '100%' }}>
              <Typography.Text type="secondary" style={{ wordBreak: 'break-all' }}>
                {tr("订阅：")}{urlById.get(record.subscriptionId) ?? record.subscriptionId} / Delivery：
                <Typography.Text code>{record.id}</Typography.Text>
              </Typography.Text>
              {record.lastError ? (
                <Typography.Text type="danger" style={{ wordBreak: 'break-all' }}>
                  {tr("最近错误：")}{record.lastError}
                </Typography.Text>
              ) : null}
              <pre style={{ margin: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                {formatPayload(record.payloadJson)}
              </pre>
            </Space>
          ),
        }}
        columns={columns}
      />
    </Card>
  );
}
