import { tr } from '../../i18n/translate';
import {
  DeleteOutlined,
  ReloadOutlined,
  SettingOutlined,
  UndoOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Flex,
  Input,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useState } from 'react';
import { backupsApi } from '../../shared/api/backups';
import type { DeletedBackup } from '../../shared/api/types';
import { formatBytes, formatDateTime } from '../../shared/format';
import { fileStateMeta, isGraceCritical, remainingGraceLabel } from '../../shared/recycleUi';
import { responsivePagination } from '../../shared/responsive';
import { RetentionSettingsModal } from '../../shared/ui/RetentionSettingsModal';
import { useResponsive } from '../../shared/useResponsive';

interface BackupRecyclePanelProps {
  appKey: string;
  canRestore: boolean;
  canPurge: boolean;
  canManageGrace: boolean;
}

/** 备份回收站：软删除行、剩余宽限、文件完好标记与恢复/清理动作。 */
export function BackupRecyclePanel({
  appKey,
  canRestore,
  canPurge,
  canManageGrace,
}: BackupRecyclePanelProps) {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const [keyword, setKeyword] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [settingsOpen, setSettingsOpen] = useState(false);

  // 上下文在 Header 切换；旧应用的筛选/分页不能带到新应用
  useEffect(() => {
    setKeyword('');
    setPage(1);
    setPageSize(20);
  }, [appKey]);

  const deleted = useQuery({
    queryKey: ['backups', 'recycle', appKey, keyword, page, pageSize],
    queryFn: ({ signal }) =>
      backupsApi.listDeletedBackups(
        { keyword: keyword.trim() || undefined, page, pageSize },
        signal,
      ),
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['backups'] });
  };

  const handleRestore = (row: DeletedBackup) => {
    modal.confirm({
      title: tr("恢复备份「{{arg0}}」？", { arg0: row.name }),
      content: tr(
        "恢复后可能超过命名空间保留上限，后续新建备份会被拒绝，直到删除旧备份。",
      ),
      okText: tr("恢复"),
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          await backupsApi.restoreBackup(row.id);
          message.success(tr("备份已恢复"));
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("恢复失败"));
        }
      },
    });
  };

  const handlePurge = (row: DeletedBackup) => {
    modal.confirm({
      title: tr("立即清理「{{arg0}}」？此操作不可恢复。", { arg0: row.name }),
      okText: tr("立即清理"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          const result = await backupsApi.purgeBackup(row.id);
          message.success(
            tr("已清理 {{arg0}} 条记录、回收 {{arg1}} 个文件", {
              arg0: result.rowsDeleted,
              arg1: result.filesDeleted,
            }),
          );
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("清理失败"));
        }
      },
    });
  };

  // purge-all 不看筛选条件；有关键词时 total 只是筛选后的条数，不能当作整体条数展示
  const hasKeyword = keyword.trim().length > 0;

  const handlePurgeAll = () => {
    modal.confirm({
      title: hasKeyword
        ? tr("清理回收站全部记录？")
        : tr("清理回收站全部 {{arg0}} 条记录？此操作不可恢复。", {
            arg0: deleted.data?.total ?? 0,
          }),
      content: hasKeyword
        ? tr("将清理回收站全部记录（不受当前筛选影响），此操作不可恢复。")
        : undefined,
      okText: tr("全部清理"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          const result = await backupsApi.purgeAllBackups();
          message.success(
            tr("已清理 {{arg0}} 条记录、回收 {{arg1}} 个文件", {
              arg0: result.rowsDeleted,
              arg1: result.filesDeleted,
            }),
          );
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("清理失败"));
        }
      },
    });
  };

  const columns: TableColumnsType<DeletedBackup> = [
    { title: tr("名称"), dataIndex: 'name', key: 'name' },
    { title: tr("命名空间"), dataIndex: 'namespaceKey', key: 'namespaceKey', width: 130 },
    {
      title: tr("类型"),
      dataIndex: 'kind',
      key: 'kind',
      width: 90,
      render: (kind: string) => <Tag>{kind}</Tag>,
    },
    {
      title: tr("大小"),
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      responsive: ['md'],
      render: (value: number) => formatBytes(value),
    },
    {
      title: tr("删除时间"),
      dataIndex: 'deletedAt',
      key: 'deletedAt',
      width: 170,
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("剩余宽限"),
      dataIndex: 'remainingGraceMinutes',
      key: 'remainingGraceMinutes',
      width: 140,
      render: (value: number) => (
        <Typography.Text type={isGraceCritical(value) ? 'danger' : undefined}>
          {remainingGraceLabel(value)}
        </Typography.Text>
      ),
    },
    {
      title: tr("文件状态"),
      dataIndex: 'fileIntact',
      key: 'fileIntact',
      width: 110,
      render: (intact: boolean) => {
        const meta = fileStateMeta(intact);
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 180,
      fixed: 'right',
      render: (_: unknown, row: DeletedBackup) => (
        <Space size="small">
          {canRestore ? (
            row.fileIntact ? (
              <Button
                type="link"
                size="small"
                icon={<UndoOutlined />}
                onClick={() => handleRestore(row)}
              >
                {tr("恢复")}
              </Button>
            ) : (
              <Tooltip title={tr("文件已缺失，只能清理")}>
                <Button type="link" size="small" icon={<UndoOutlined />} disabled>
                  {tr("恢复")}
                </Button>
              </Tooltip>
            )
          ) : null}
          {canPurge ? (
            <Button
              type="link"
              size="small"
              danger
              icon={<DeleteOutlined />}
              onClick={() => handlePurge(row)}
            >
              {tr("立即清理")}
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size={12} style={{ display: 'flex' }}>
      <Flex wrap gap={8} align="center">
        <Input
          allowClear
          placeholder={tr("按名称或节点名过滤回收站")}
          style={{ width: 240, maxWidth: '100%' }}
          value={keyword}
          onChange={(event) => {
            setKeyword(event.target.value);
            setPage(1);
          }}
        />
        <Button icon={<ReloadOutlined />} onClick={() => void deleted.refetch()}>
          {tr("刷新")}
        </Button>
        <span style={{ flex: 1 }} />
        {canManageGrace ? (
          <Button icon={<SettingOutlined />} onClick={() => setSettingsOpen(true)}>
            {tr("清理宽限期")}
          </Button>
        ) : null}
        {canPurge ? (
          <Button
            danger
            icon={<DeleteOutlined />}
            disabled={!hasKeyword && (deleted.data?.total ?? 0) === 0}
            onClick={handlePurgeAll}
          >
            {tr("全部清理")}
          </Button>
        ) : null}
      </Flex>

      {deleted.isError ? (
        <Alert
          type="error"
          showIcon
          message={(deleted.error as Error).message}
          action={
            <Button size="small" onClick={() => void deleted.refetch()}>
              {tr("重试")}
            </Button>
          }
        />
      ) : null}

      <Table<DeletedBackup>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={deleted.isPending}
        dataSource={deleted.data?.items ?? []}
        columns={columns}
        pagination={responsivePagination(isMobile, {
          current: deleted.data?.page ?? page,
          pageSize: deleted.data?.pageSize ?? pageSize,
          total: deleted.data?.total ?? 0,
          showTotal: (total: number) => tr("共 {{arg0}} 条待清理记录", { arg0: total }),
          onChange: (nextPage: number, nextPageSize: number) => {
            setPage(nextPageSize === pageSize ? nextPage : 1);
            setPageSize(nextPageSize);
          },
        })}
        locale={{ emptyText: tr("回收站为空") }}
      />

      <RetentionSettingsModal open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </Space>
  );
}
