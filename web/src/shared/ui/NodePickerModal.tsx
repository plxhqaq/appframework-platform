import { formatDateTime } from '../format';
import { tr } from '../../i18n/translate';
import { ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Flex, Input, Modal, Space, Table, Tag, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { nodesApi } from '../api/nodes';
import type { NodeSummary } from '../api/types';
import { responsivePagination, responsiveWidth } from '../responsive';
import { useResponsive } from '../useResponsive';
import { isNodeLicenseSelectable } from '../nodeSelection';

const DEFAULT_PAGE_SIZE = 10;

interface NodePickerModalProps {
  open: boolean;
  selected: ReadonlyMap<string, NodeSummary>;
  onConfirm: (selected: Map<string, NodeSummary>) => void;
  onCancel: () => void;
}

/**
 * 节点选择弹窗（签发授权绑定机器码）。
 *
 * 授权席位锚点是机器码，离线或运行中的节点也能签发；停用节点不可选。
 * 节点可能很多，服务端分页 + 关键字筛选。
 */
export function NodePickerModal({ open, selected, onConfirm, onCancel }: NodePickerModalProps) {
  const { isMobile } = useResponsive();
  const [draft, setDraft] = useState<Map<string, NodeSummary>>(new Map());
  const [keyword, setKeyword] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);

  useEffect(() => {
    if (open) {
      setDraft(new Map(selected));
      setKeyword(undefined);
      setPage(1);
    }
  }, [open, selected]);

  const nodes = useQuery({
    queryKey: ['nodes', 'list', 'picker', keyword, page, pageSize],
    queryFn: ({ signal }) => nodesApi.listNodes({ keyword, page, pageSize }, signal),
    enabled: open,
  });

  const columns: TableColumnsType<NodeSummary> = [
    {
      title: tr("节点"),
      key: 'displayName',
      ellipsis: true,
      render: (_: unknown, row: NodeSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{row.displayName || tr("(未命名)")}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {row.shortId}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: tr("机器码"),
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 170,
      responsive: ['md'],
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: tr("IP 地址"),
      dataIndex: 'ipAddress',
      key: 'ipAddress',
      width: 140,
      responsive: ['lg'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("应用"),
      dataIndex: 'appKeys',
      key: 'appKeys',
      width: 180,
      responsive: ['lg'],
      render: (value: string[]) =>
        value.length > 0 ? (
          <Space size={4} wrap>
            {value.map((appKey) => (
              <Tag key={appKey}>{appKey}</Tag>
            ))}
          </Space>
        ) : (
          '-'
        ),
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 130,
      render: (_: unknown, row: NodeSummary) => (
        <Space size={4} wrap>
          {!row.isEnabled ? (
            <Tag color="red">{tr("停用")}</Tag>
          ) : row.onlineStatus === 'offline' ? (
            <Tag>{tr("离线")}</Tag>
          ) : (
            <Tag color={row.stationStatus === 'idle' ? 'green' : 'orange'}>
              {row.stationStatus === 'idle' ? tr("在线/待机") : tr("运行中")}
            </Tag>
          )}
        </Space>
      ),
    },
    {
      title: tr("最后心跳"),
      dataIndex: 'lastHeartbeatAt',
      key: 'lastHeartbeatAt',
      width: 170,
      responsive: ['lg'],
      render: (value: string | null) => (value ? formatDateTime(value) : '-'),
    },
  ];

  return (
    <Modal
      open={open}
      title={tr("选择节点")}
      width={responsiveWidth(isMobile, 960)}
      onCancel={onCancel}
      onOk={() => onConfirm(draft)}
      okText={tr("确定")}
      okButtonProps={{ disabled: draft.size === 0 }}
      cancelText={tr("取消")}
    >
      <Space direction="vertical" size={12} style={{ display: 'flex' }}>
        <Flex wrap gap={8}>
          <Input.Search
            allowClear
            placeholder={tr("节点名 / 机器码 / IP")}
            style={{ width: 260, maxWidth: '100%' }}
            onSearch={(value) => {
              setKeyword(value.trim() || undefined);
              setPage(1);
            }}
          />
          <Button icon={<ReloadOutlined />} onClick={() => void nodes.refetch()}>
            {tr("刷新")}</Button>
          <Typography.Text type="secondary" style={{ alignSelf: 'center' }}>
            {tr("离线与运行中节点也可选择；停用节点不可选")}</Typography.Text>
        </Flex>

        {nodes.isError ? (
          <Alert
            type="error"
            showIcon
            message={(nodes.error as Error).message}
            action={
              <Button size="small" onClick={() => void nodes.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}

        <Table<NodeSummary>
          rowKey="id"
          size="small"
          loading={nodes.isPending}
          dataSource={nodes.data?.items ?? []}
          columns={columns}
          rowSelection={{
            type: 'radio',
            selectedRowKeys: [...draft.keys()],
            onChange: (_keys: React.Key[], rows: NodeSummary[]) =>
              setDraft(new Map(rows.map((row) => [row.id, row]))),
            getCheckboxProps: (row: NodeSummary) => ({ disabled: !isNodeLicenseSelectable(row) }),
          }}
          scroll={{ x: 'max-content' }}
          pagination={responsivePagination(isMobile, {
            current: nodes.data?.page ?? page,
            pageSize: nodes.data?.pageSize ?? pageSize,
            total: nodes.data?.total ?? 0,
            showTotal: (total: number) => tr("共 {{arg0}} 个节点", { arg0: total }),
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: tr("没有符合条件的节点") }}
        />
      </Space>
    </Modal>
  );
}
