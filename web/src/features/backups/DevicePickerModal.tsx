import { ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Flex, Input, Modal, Space, Table, Tag, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { devicesApi } from '../../shared/api/devices';
import type { DeviceSummary } from '../../shared/api/types';
import { responsivePagination, responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { isDeviceSelectable } from './deviceSelection';

const DEFAULT_PAGE_SIZE = 10;

interface DevicePickerModalProps {
  open: boolean;
  appKey: string | null;
  selected: ReadonlyMap<string, DeviceSummary>;
  onConfirm: (selected: Map<string, DeviceSummary>) => void;
  onCancel: () => void;
}

/**
 * 在线设备选择弹窗。
 *
 * 设备可能超过 50 台，所以不使用下拉框：服务端分页 + 关键字/分支筛选，
 * 勾选跨页保留；运行中/停用设备禁止勾选（服务端仍会二次门禁）。
 */
export function DevicePickerModal({
  open,
  appKey,
  selected,
  onConfirm,
  onCancel,
}: DevicePickerModalProps) {
  const { isMobile } = useResponsive();
  const [draft, setDraft] = useState<Map<string, DeviceSummary>>(new Map());
  const [keyword, setKeyword] = useState<string | undefined>();
  const [branch, setBranch] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);

  useEffect(() => {
    if (open) {
      setDraft(new Map(selected));
      setKeyword(undefined);
      setBranch(undefined);
      setPage(1);
    }
  }, [open, selected]);

  const devices = useQuery({
    queryKey: ['devices', 'list', 'push-picker', appKey, keyword, branch, page, pageSize],
    queryFn: ({ signal }) =>
      devicesApi.listDevices({ onlineStatus: 'online', keyword, branch, page, pageSize }, signal),
    enabled: open && appKey !== null,
    refetchInterval: open ? 15000 : false,
  });

  const columns: TableColumnsType<DeviceSummary> = [
    { title: '设备名', dataIndex: 'displayName', key: 'displayName', ellipsis: true },
    {
      title: '机器码',
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 170,
      responsive: ['md'],
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: '分支',
      dataIndex: 'branch',
      key: 'branch',
      width: 100,
      responsive: ['lg'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: '状态',
      key: 'status',
      width: 150,
      render: (_: unknown, row: DeviceSummary) => (
        <Space size={4} wrap>
          {row.isEnabled ? (
            <Tag color={row.stationStatus === 'idle' ? 'green' : 'orange'}>
              {row.stationStatus === 'idle' ? '在线/待机' : '运行中'}
            </Tag>
          ) : (
            <Tag>已停用</Tag>
          )}
          {row.isEnabled && row.stationStatus !== 'idle' ? (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              不可下发
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: '最后心跳',
      dataIndex: 'lastHeartbeatAt',
      key: 'lastHeartbeatAt',
      width: 170,
      responsive: ['lg'],
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
  ];

  // rows 只含当前页仍勾选的行：先保留其它页的选择，再用当前页结果覆盖本页。
  const handleSelectionChange = (_keys: React.Key[], rows: DeviceSummary[]) => {
    const currentIds = new Set((devices.data?.items ?? []).map((item) => item.id));
    const next = new Map<string, DeviceSummary>();
    draft.forEach((device, id) => {
      if (!currentIds.has(id)) {
        next.set(id, device);
      }
    });
    rows.forEach((row) => next.set(row.id, row));
    setDraft(next);
  };

  const rowSelection = {
    selectedRowKeys: [...draft.keys()],
    preserveSelectedRowKeys: true,
    onChange: handleSelectionChange,
    getCheckboxProps: (row: DeviceSummary) => ({ disabled: !isDeviceSelectable(row) }),
  };

  return (
    <Modal
      open={open}
      title="选择目标设备"
      width={responsiveWidth(isMobile, 920)}
      onCancel={onCancel}
      onOk={() => onConfirm(draft)}
      okText={`确定（已选 ${draft.size} 台）`}
      okButtonProps={{ disabled: draft.size === 0 }}
      cancelText="取消"
    >
      {appKey === null ? (
        <Alert type="warning" showIcon message="当前账号没有可用的应用上下文" />
      ) : (
        <Space direction="vertical" size={12} style={{ display: 'flex' }}>
          <Flex wrap gap={8}>
            <Input.Search
              allowClear
              placeholder="设备名 / 机器码 / IP"
              style={{ width: 240, maxWidth: '100%' }}
              onSearch={(value) => {
                setKeyword(value.trim() || undefined);
                setPage(1);
              }}
            />
            <Input
              allowClear
              placeholder="分支"
              style={{ width: 160, maxWidth: '100%' }}
              value={branch}
              onChange={(event) => {
                setBranch(event.target.value.trim() || undefined);
                setPage(1);
              }}
            />
            <Button icon={<ReloadOutlined />} onClick={() => void devices.refetch()}>
              刷新
            </Button>
            <Typography.Text type="secondary" style={{ alignSelf: 'center' }}>
              仅显示在线设备；运行中与停用设备不可勾选
            </Typography.Text>
          </Flex>

          {devices.isError ? (
            <Alert
              type="error"
              showIcon
              message={(devices.error as Error).message}
              action={
                <Button size="small" onClick={() => void devices.refetch()}>
                  重试
                </Button>
              }
            />
          ) : null}

          <Table<DeviceSummary>
            rowKey="id"
            size="small"
            loading={devices.isPending}
            dataSource={devices.data?.items ?? []}
            columns={columns}
            rowSelection={rowSelection}
            scroll={{ x: 'max-content' }}
            pagination={responsivePagination(isMobile, {
              current: devices.data?.page ?? page,
              pageSize: devices.data?.pageSize ?? pageSize,
              total: devices.data?.total ?? 0,
              showTotal: (total: number) => `共 ${total} 台在线设备`,
              onChange: (nextPage: number, nextPageSize: number) => {
                setPage(nextPageSize === pageSize ? nextPage : 1);
                setPageSize(nextPageSize);
              },
            })}
            locale={{ emptyText: '没有符合条件的在线设备' }}
          />
        </Space>
      )}
    </Modal>
  );
}
