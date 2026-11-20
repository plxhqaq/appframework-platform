import { formatDateTime } from '../../shared/format';
import { tr } from '../../i18n/translate';
import { ReloadOutlined, SearchOutlined, UndoOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Card, DatePicker, Flex, Input, Select, Space, Table, Tag, Tooltip, Typography } from 'antd';
import type { Dayjs } from 'dayjs';
import { useState } from 'react';
import { auditApi } from '../../shared/api/audit';
import type { AuditListQuery } from '../../shared/api/audit';
import type { AuditLog } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
import { responsivePagination } from '../../shared/responsive';
import { renderTableExpandIcon } from '../../shared/ui/TableExpandIcon';
import { useResponsive } from '../../shared/useResponsive';
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
  const { isMobile } = useResponsive();

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
        message={tr("缺少 audit.read 权限")}
        description={tr("审计日志只在服务端强制权限后可见，请联系平台管理员分配角色。")}
      />
    );
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Card size="small">
        <Flex wrap gap={8} align="center">
          <Input
            allowClear
            placeholder={tr("应用键（留空=全部）")}
            style={{ width: 160, maxWidth: '100%' }}
            value={draft.appKey}
            onChange={(e) => setDraft((current) => ({ ...current, appKey: e.target.value }))}
          />
          <Input
            allowClear
            placeholder={tr("动作，如 identity.login")}
            style={{ width: 200, maxWidth: '100%' }}
            value={draft.action}
            onChange={(e) => setDraft((current) => ({ ...current, action: e.target.value }))}
          />
          <Input
            allowClear
            placeholder={tr("操作者")}
            style={{ width: 150, maxWidth: '100%' }}
            value={draft.actor}
            onChange={(e) => setDraft((current) => ({ ...current, actor: e.target.value }))}
          />
          <Select
            allowClear
            placeholder={tr("结果")}
            style={{ width: 120, maxWidth: '100%' }}
            value={draft.result}
            onChange={(value) => setDraft((current) => ({ ...current, result: value }))}
            options={[
              { value: 'success', label: tr("成功") },
              { value: 'failure', label: tr("失败") },
            ]}
          />
          <Input
            allowClear
            placeholder={tr("关键字（目标/说明）")}
            style={{ width: 190, maxWidth: '100%' }}
            value={draft.keyword}
            onChange={(e) => setDraft((current) => ({ ...current, keyword: e.target.value }))}
          />
          <RangePicker
            showTime
            style={{ maxWidth: '100%' }}
            value={range}
            onChange={(value) => setRange(value as [Dayjs | null, Dayjs | null] | null)}
          />
          <Button type="primary" icon={<SearchOutlined />} onClick={handleSearch}>
            {tr("查询")}</Button>
          <Button icon={<UndoOutlined />} onClick={handleReset}>
            {tr("重置")}</Button>
          <Button
            icon={<ReloadOutlined />}
            aria-label={tr("刷新审计日志")}
            onClick={() => void logs.refetch()}
            loading={logs.isFetching}
          >
            {tr("刷新")}</Button>
        </Flex>
      </Card>

      <Card
        title={tr("审计日志")}
        size="small"
        extra={<Typography.Text type="secondary">{tr("共")}{logs.data?.total ?? 0} {tr("条")}</Typography.Text>}
      >
        {logs.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={(logs.error as Error).message}
            action={
              <Button size="small" onClick={() => void logs.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<AuditLog>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={logs.isFetching}
          dataSource={logs.data?.items ?? []}
          pagination={responsivePagination(isMobile, {
            current: page,
            pageSize,
            total: logs.data?.total ?? 0,
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPage);
              setPageSize(nextPageSize);
            },
          })}
          expandable={{
            expandIcon: renderTableExpandIcon,
            expandedRowRender: (record) => <AuditDetail record={record} />,
          }}
          columns={[
            {
              title: tr("时间"),
              dataIndex: 'createdAt',
              width: 170,
              render: (value: string) => formatDateTime(value),
            },
            {
              title: tr("应用"),
              dataIndex: 'appKey',
              width: 110,
              responsive: ['md'],
              render: (value: string | null) => value ?? '-',
            },
            { title: tr("动作"), dataIndex: 'action', width: 220 },
            {
              title: tr("操作者"),
              dataIndex: 'actorName',
              width: 140,
              responsive: ['md'],
              render: (value: string | null, record) => value ?? record.actorId ?? '-',
            },
            {
              title: tr("目标"),
              dataIndex: 'targetType',
              ellipsis: { showTitle: false },
              render: (value: string | null, record) => {
                if (!value) {
                  return '-';
                }
                const target = `${value} ${record.targetId ?? ''}`.trim();
                return (
                  <Tooltip title={target}>
                    <span>{target}</span>
                  </Tooltip>
                );
              },
            },
            {
              title: tr("结果"),
              dataIndex: 'result',
              width: 90,
              render: (value: string) => (
                <Tag color={value === 'success' ? 'success' : 'error'}>
                  {value === 'success' ? tr("成功") : tr("失败")}
                </Tag>
              ),
            },
          ]}
        />
      </Card>
    </Space>
  );
}
