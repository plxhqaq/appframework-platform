import { ReloadOutlined, SearchOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Card, DatePicker, Input, Select, Space, Table, Tag, Typography } from 'antd';
import type { Dayjs } from 'dayjs';
import { useState } from 'react';
import { auditApi } from '../../shared/api/audit';
import type { AuditListQuery } from '../../shared/api/audit';
import type { AuditLog } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';

const { RangePicker } = DatePicker;

/** 展开行里的详情：JSON 原样展示，不解析——审计的效力就在于留痕内容与库中一致。 */
function AuditDetail({ record }: { record: AuditLog }) {
  return (
    <Space direction="vertical" size={4} style={{ width: '100%' }}>
      {record.message ? <Typography.Text>{record.message}</Typography.Text> : null}
      <Typography.Text type="secondary">
        IP：{record.ipAddress ?? '-'} / UA：{record.userAgent ?? '-'} / TraceId：{record.traceId ?? '-'}
      </Typography.Text>
      {record.detailJson ? (
        <Typography.Paragraph>
          <pre style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{record.detailJson}</pre>
        </Typography.Paragraph>
      ) : null}
    </Space>
  );
}

/**
 * 审计查询页。
 *
 * 只读：任何写路径都不在这里，排查完成后要改数据仍回到各业务页，
 * 避免「看着日志在列表里改状态」这种绕过审计的操作。
 */
export function AuditPage() {
  const canRead = useAuthStore((state) => state.hasPermission(PERMISSIONS.auditRead));

  const [filters, setFilters] = useState<Omit<AuditListQuery, 'page' | 'pageSize'>>({});
  const [draft, setDraft] = useState<Pick<AuditListQuery, 'appKey' | 'action' | 'actor' | 'result' | 'keyword'>>({});
  const [range, setRange] = useState<[Dayjs | null, Dayjs | null] | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  const query: AuditListQuery = {
    ...filters,
    ...draft,
    from: range?.[0] ? range[0].toISOString() : undefined,
    to: range?.[1] ? range[1].toISOString() : undefined,
    page,
    pageSize,
  };

  const logs = useQuery({
    queryKey: ['audit', 'list', query],
    queryFn: ({ signal }) => auditApi.list(query, signal),
    enabled: canRead,
  });

  const handleSearch = () => {
    setPage(1);
    setFilters({ ...draft });
  };

  const handleReset = () => {
    setDraft({});
    setFilters({});
    setRange(null);
    setPage(1);
  };

  if (!canRead) {
    return (
      <Alert
        type="warning"
        showIcon
        message="缺少 audit.read 权限"
        description="审计日志只在服务端强制权限后可见，请联系平台管理员分配角色。"
      />
    );
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Card size="small">
        <Space wrap>
          <Input
            allowClear
            placeholder="应用键（留空=全部）"
            style={{ width: 160 }}
            value={draft.appKey}
            onChange={(e) => setDraft((current) => ({ ...current, appKey: e.target.value }))}
          />
          <Input
            allowClear
            placeholder="动作，如 identity.login"
            style={{ width: 200 }}
            value={draft.action}
            onChange={(e) => setDraft((current) => ({ ...current, action: e.target.value }))}
          />
          <Input
            allowClear
            placeholder="操作者"
            style={{ width: 150 }}
            value={draft.actor}
            onChange={(e) => setDraft((current) => ({ ...current, actor: e.target.value }))}
          />
          <Select
            allowClear
            placeholder="结果"
            style={{ width: 120 }}
            value={draft.result}
            onChange={(value) => setDraft((current) => ({ ...current, result: value }))}
            options={[
              { value: 'success', label: '成功' },
              { value: 'failure', label: '失败' },
            ]}
          />
          <Input
            allowClear
            placeholder="关键字（目标/说明）"
            style={{ width: 190 }}
            value={draft.keyword}
            onChange={(e) => setDraft((current) => ({ ...current, keyword: e.target.value }))}
          />
          <RangePicker
            showTime
            value={range}
            onChange={(value) => setRange(value as [Dayjs | null, Dayjs | null] | null)}
          />
          <Button type="primary" icon={<SearchOutlined />} onClick={handleSearch}>
            查询
          </Button>
          <Button onClick={handleReset}>重置</Button>
          <Button
            icon={<ReloadOutlined />}
            onClick={() => void logs.refetch()}
            loading={logs.isFetching}
          >
            刷新
          </Button>
        </Space>
      </Card>

      <Card
        title="审计日志"
        size="small"
        extra={<Typography.Text type="secondary">共 {logs.data?.total ?? 0} 条</Typography.Text>}
      >
        <Table<AuditLog>
          rowKey="id"
          size="small"
          loading={logs.isFetching}
          dataSource={logs.data?.items ?? []}
          pagination={{
            current: page,
            pageSize,
            total: logs.data?.total ?? 0,
            showSizeChanger: true,
            onChange: (nextPage, nextPageSize) => {
              setPage(nextPage);
              setPageSize(nextPageSize);
            },
          }}
          expandable={{ expandedRowRender: (record) => <AuditDetail record={record} /> }}
          columns={[
            {
              title: '时间',
              dataIndex: 'createdAt',
              width: 170,
              render: (value: string) => new Date(value).toLocaleString(),
            },
            { title: '应用', dataIndex: 'appKey', width: 110, render: (value: string | null) => value ?? '-' },
            { title: '动作', dataIndex: 'action', width: 220 },
            {
              title: '操作者',
              dataIndex: 'actorName',
              width: 140,
              render: (value: string | null, record) => value ?? record.actorId ?? '-',
            },
            {
              title: '目标',
              dataIndex: 'targetType',
              ellipsis: true,
              render: (value: string | null, record) =>
                value ? `${value} ${record.targetId ?? ''}`.trim() : '-',
            },
            {
              title: '结果',
              dataIndex: 'result',
              width: 90,
              render: (value: string) => (
                <Tag color={value === 'success' ? 'success' : 'error'}>
                  {value === 'success' ? '成功' : '失败'}
                </Tag>
              ),
            },
          ]}
        />
      </Card>
    </Space>
  );
}
