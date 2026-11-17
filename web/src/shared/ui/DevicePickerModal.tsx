import { formatDateTime } from '../format';
import { tr } from '../../i18n/translate';
import { ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Flex, Input, Modal, Space, Table, Tag, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { devicesApi } from '../api/devices';
import type { DeviceSummary } from '../api/types';
import { responsivePagination, responsiveWidth } from '../responsive';
import { useResponsive } from '../useResponsive';
import { isDeviceLicenseSelectable, isDeviceSelectable } from '../deviceSelection';

const DEFAULT_PAGE_SIZE = 10;

interface DevicePickerModalProps {
  open: boolean;
  appKey: string | null;
  selected: ReadonlyMap<string, DeviceSummary>;
  onConfirm: (selected: Map<string, DeviceSummary>) => void;
  onCancel: () => void;
  /** 单选模式：只允许选一台（如签发授权绑定机器码）。 */
  single?: boolean;
  /** push：只列在线且待机的设备；license：授权按机器码，离线/运行中设备也可选。 */
  mode?: 'push' | 'license';
  title?: string;
}

/**
 * 设备选择弹窗。
 *
 * 设备可能超过 50 台，所以不使用下拉框：服务端分页 + 关键字/分支筛选，勾选跨页保留。
 * push 模式只允许在线待机设备；license 模式允许离线设备（授权主体是机器码，不要求在线）。
 */
export function DevicePickerModal({
  open,
  appKey,
  selected,
  onConfirm,
  onCancel,
  single = false,
  mode = 'push',
  title,
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
    queryKey: ['devices', 'list', 'picker', mode, appKey, keyword, branch, page, pageSize],
    queryFn: ({ signal }) =>
      devicesApi.listDevices(
        {
          onlineStatus: mode === 'push' ? 'online' : undefined,
          keyword,
          branch,
          page,
          pageSize,
        },
        signal,
      ),
    enabled: open && appKey !== null,
    refetchInterval: open && mode === 'push' ? 15000 : false,
  });

  const canSelect = mode === 'license' ? isDeviceLicenseSelectable : isDeviceSelectable;

  const columns: TableColumnsType<DeviceSummary> = [
    { title: tr("设备名"), dataIndex: 'displayName', key: 'displayName', ellipsis: true },
    {
      title: tr("机器码"),
      dataIndex: 'machineCode',
      key: 'machineCode',
      width: 170,
      responsive: ['md'],
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: tr("分支"),
      dataIndex: 'branch',
      key: 'branch',
      width: 100,
      responsive: ['lg'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 150,
      render: (_: unknown, row: DeviceSummary) => (
        <Space size={4} wrap>
          {!row.isEnabled ? (
            <Tag>{tr("已停用")}</Tag>
          ) : row.onlineStatus === 'offline' ? (
            <Tag>{tr("离线")}</Tag>
          ) : (
            <Tag color={row.stationStatus === 'idle' ? 'green' : 'orange'}>
              {row.stationStatus === 'idle' ? tr("在线/待机") : tr("运行中")}
            </Tag>
          )}
          {mode === 'push' && row.isEnabled && !isDeviceSelectable(row) ? (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {tr("不可下发")}</Typography.Text>
          ) : null}
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

  // rows 只含当前页仍勾选的行：先保留其它页的选择，再用当前页结果覆盖本页。
  const handleSelectionChange = (_keys: React.Key[], rows: DeviceSummary[]) => {
    if (single) {
      setDraft(new Map(rows.map((row) => [row.id, row])));
      return;
    }

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
    type: single ? ('radio' as const) : ('checkbox' as const),
    selectedRowKeys: [...draft.keys()],
    preserveSelectedRowKeys: true,
    onChange: handleSelectionChange,
    getCheckboxProps: (row: DeviceSummary) => ({ disabled: !canSelect(row) }),
  };

  return (
    <Modal
      open={open}
      title={title ?? tr("选择目标设备")}
      width={responsiveWidth(isMobile, 920)}
      onCancel={onCancel}
      onOk={() => onConfirm(draft)}
      okText={single ? tr("确定") : tr("确定（已选 {{arg0}} 台）", { arg0: draft.size })}
      okButtonProps={{ disabled: draft.size === 0 }}
      cancelText={tr("取消")}
    >
      {appKey === null ? (
        <Alert type="warning" showIcon message={tr("当前账号没有可用的应用上下文")} />
      ) : (
        <Space direction="vertical" size={12} style={{ display: 'flex' }}>
          <Flex wrap gap={8}>
            <Input.Search
              allowClear
              placeholder={tr("设备名 / 机器码 / IP")}
              style={{ width: 240, maxWidth: '100%' }}
              onSearch={(value) => {
                setKeyword(value.trim() || undefined);
                setPage(1);
              }}
            />
            <Input
              allowClear
              placeholder={tr("分支")}
              style={{ width: 160, maxWidth: '100%' }}
              value={branch}
              onChange={(event) => {
                setBranch(event.target.value.trim() || undefined);
                setPage(1);
              }}
            />
            <Button icon={<ReloadOutlined />} onClick={() => void devices.refetch()}>
              {tr("刷新")}</Button>
            <Typography.Text type="secondary" style={{ alignSelf: 'center' }}>
              {mode === 'push'
                ? tr("仅显示在线设备；运行中与停用设备不可勾选")
                : tr("离线与运行中设备也可选择；停用设备不可选")}</Typography.Text>
          </Flex>

          {devices.isError ? (
            <Alert
              type="error"
              showIcon
              message={(devices.error as Error).message}
              action={
                <Button size="small" onClick={() => void devices.refetch()}>
                  {tr("重试")}</Button>
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
              showTotal: (total: number) =>
                mode === 'push'
                  ? tr("共 {{arg0}} 台在线设备", { arg0: total })
                  : tr("共 {{arg0}} 台设备", { arg0: total }),
              onChange: (nextPage: number, nextPageSize: number) => {
                setPage(nextPageSize === pageSize ? nextPage : 1);
                setPageSize(nextPageSize);
              },
            })}
            locale={{
              emptyText:
                mode === 'push'
                  ? tr("没有符合条件的在线设备")
                  : tr("没有符合条件的设备"),
            }}
          />
        </Space>
      )}
    </Modal>
  );
}
