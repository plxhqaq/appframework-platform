import { formatDateTime } from '../format';
import { tr } from '../../i18n/translate';
import { ReloadOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Flex, Input, Modal, Space, Table, Tag, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { installationLabel, installationsApi } from '../api/installations';
import type { InstallationSummary } from '../api/types';
import { responsivePagination, responsiveWidth } from '../responsive';
import { useResponsive } from '../useResponsive';
import { isInstallationAvailable, isInstallationSelectable } from '../installationSelection';

const DEFAULT_PAGE_SIZE = 10;

interface InstallationPickerModalProps {
  open: boolean;
  appKey: string | null;
  selected: ReadonlyMap<string, InstallationSummary>;
  onConfirm: (selected: Map<string, InstallationSummary>) => void;
  onCancel: () => void;
  /** 单选模式：只允许选一个（如备份来源）。 */
  single?: boolean;
  /** push：只列在线待机实例；any：所有有效启用的实例（离线也可选）。 */
  mode?: 'push' | 'any';
  title?: string;
}

/**
 * 安装实例选择弹窗（备份下发 / 备份来源）。
 *
 * 实例可能超过 50 条，所以不使用下拉框：服务端分页 + 关键字/分支筛选，勾选跨页保留。
 * push 模式只允许在线待机；any 模式允许离线实例（来源只做快照关联）。
 */
export function InstallationPickerModal({
  open,
  appKey,
  selected,
  onConfirm,
  onCancel,
  single = false,
  mode = 'push',
  title,
}: InstallationPickerModalProps) {
  const { isMobile } = useResponsive();
  const [draft, setDraft] = useState<Map<string, InstallationSummary>>(new Map());
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

  const installations = useQuery({
    queryKey: ['installations', 'list', 'picker', mode, appKey, keyword, branch, page, pageSize],
    queryFn: ({ signal }) =>
      installationsApi.listInstallations(
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
    // 在线态跃迁由 InstallationStateChanged 推送失效；打开时仅保留 60s 兜底。
    refetchInterval: open && mode === 'push' ? 60_000 : false,
  });

  const canSelect = mode === 'push' ? isInstallationSelectable : isInstallationAvailable;

  const columns: TableColumnsType<InstallationSummary> = [
    {
      title: tr("安装实例 / 节点"),
      key: 'installation',
      ellipsis: true,
      render: (_: unknown, row: InstallationSummary) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{installationLabel(row)}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {tr("节点：{{arg0}}", { arg0: row.nodeDisplayName })}
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
      title: tr("分支"),
      dataIndex: 'branch',
      key: 'branch',
      width: 100,
      responsive: ['lg'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("框架版本"),
      dataIndex: 'frameworkVersion',
      key: 'frameworkVersion',
      width: 110,
      responsive: ['lg'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 160,
      render: (_: unknown, row: InstallationSummary) => (
        <Space size={4} wrap>
          {!row.nodeEnabled ? (
            <Tag color="red">{tr("节点停用")}</Tag>
          ) : !row.isEnabled ? (
            <Tag>{tr("已停用")}</Tag>
          ) : row.onlineStatus === 'offline' ? (
            <Tag>{tr("离线")}</Tag>
          ) : (
            <Tag color={row.stationStatus === 'idle' ? 'green' : 'orange'}>
              {row.stationStatus === 'idle' ? tr("在线/待机") : tr("运行中")}
            </Tag>
          )}
          {mode === 'push' && isInstallationAvailable(row) && !isInstallationSelectable(row) ? (
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

  const handleSelectionChange = (_keys: React.Key[], rows: InstallationSummary[]) => {
    if (single) {
      setDraft(new Map(rows.map((row) => [row.id, row])));
      return;
    }

    const currentIds = new Set((installations.data?.items ?? []).map((item) => item.id));
    const next = new Map<string, InstallationSummary>();
    draft.forEach((installation, id) => {
      if (!currentIds.has(id)) {
        next.set(id, installation);
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
    getCheckboxProps: (row: InstallationSummary) => ({ disabled: !canSelect(row) }),
  };

  return (
    <Modal
      open={open}
      title={title ?? tr("选择目标安装实例")}
      width={responsiveWidth(isMobile, 960)}
      onCancel={onCancel}
      onOk={() => onConfirm(draft)}
      okText={single ? tr("确定") : tr("确定（已选 {{arg0}} 个）", { arg0: draft.size })}
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
              placeholder={tr("节点名 / 机器码 / IP")}
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
            <Button icon={<ReloadOutlined />} onClick={() => void installations.refetch()}>
              {tr("刷新")}</Button>
            <Typography.Text type="secondary" style={{ alignSelf: 'center' }}>
              {mode === 'push'
                ? tr("仅显示在线实例；运行中与停用实例不可勾选")
                : tr("离线实例也可选择；停用实例不可选")}</Typography.Text>
          </Flex>

          {installations.isError ? (
            <Alert
              type="error"
              showIcon
              message={(installations.error as Error).message}
              action={
                <Button size="small" onClick={() => void installations.refetch()}>
                  {tr("重试")}</Button>
              }
            />
          ) : null}

          <Table<InstallationSummary>
            rowKey="id"
            size="small"
            loading={installations.isPending}
            dataSource={installations.data?.items ?? []}
            columns={columns}
            rowSelection={rowSelection}
            scroll={{ x: 'max-content' }}
            pagination={responsivePagination(isMobile, {
              current: installations.data?.page ?? page,
              pageSize: installations.data?.pageSize ?? pageSize,
              total: installations.data?.total ?? 0,
              showTotal: (total: number) =>
                mode === 'push'
                  ? tr("共 {{arg0}} 个在线实例", { arg0: total })
                  : tr("共 {{arg0}} 个安装实例", { arg0: total }),
              onChange: (nextPage: number, nextPageSize: number) => {
                setPage(nextPageSize === pageSize ? nextPage : 1);
                setPageSize(nextPageSize);
              },
            })}
            locale={{
              emptyText:
                mode === 'push'
                  ? tr("没有符合条件的在线实例")
                  : tr("没有符合条件的安装实例"),
            }}
          />
        </Space>
      )}
    </Modal>
  );
}
