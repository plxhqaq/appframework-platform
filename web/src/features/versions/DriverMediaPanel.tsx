import { tr } from '../../i18n/translate';
import { CloudUploadOutlined, DownloadOutlined, ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Input,
  Space,
  Switch,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { DriverMedia } from '../../shared/api/types';
import { formatBytes, formatDateTime } from '../../shared/format';
import { responsivePagination } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { startDownload } from '../../shared/download';
import { mediaVersionLabel } from './packageUi';
import { MediaUploadModal } from './MediaUploadModal';

interface DriverMediaPanelProps {
  appKey: string | null;
  canUpload: boolean;
  canDownload: boolean;
  canArchive: boolean;
}

/**
 * 驱动媒体面板：全量安装包的媒体按组件分包独立管理（一份媒体键可存多个版本，启用版本唯一），
 * 供「下载完整包」组合与后续版本复用，避免每个全量包重复上传相同驱动。
 */
export function DriverMediaPanel({ appKey, canUpload, canDownload, canArchive }: DriverMediaPanelProps) {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const [mediaKeyFilter, setMediaKeyFilter] = useState('');
  const [activeOnly, setActiveOnly] = useState(false);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const media = useQuery({
    queryKey: ['versions', 'media', appKey, mediaKeyFilter, activeOnly, page, pageSize],
    queryFn: ({ signal }) =>
      versionsApi.listMedia(
        {
          page,
          pageSize,
          mediaKey: mediaKeyFilter.trim() || undefined,
          activeOnly: activeOnly || undefined,
        },
        signal,
      ),
    enabled: appKey !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['versions', 'media'] });
  };

  const activate = useMutation({
    mutationFn: (id: string) => versionsApi.activateMedia(id),
    onSuccess: async (activated) => {
      message.success(
        tr("已启用 {{arg0}}/{{arg1}}", { arg0: activated.mediaKey, arg1: mediaVersionLabel(activated.version) }),
      );
      await refresh();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const remove = useMutation({
    mutationFn: (id: string) => versionsApi.deleteMedia(id),
    onSuccess: async (deleted) => {
      message.success(
        tr("已删除媒体包 {{arg0}}/{{arg1}}", {
          arg0: deleted.mediaKey,
          arg1: mediaVersionLabel(deleted.version),
        }),
      );
      await refresh();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const download = async (row: DriverMedia) => {
    setDownloadingId(row.id);
    try {
      const ticket = await versionsApi.createMediaDownloadTicket(row.id);
      startDownload(ticket.url);
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("下载失败"));
    } finally {
      setDownloadingId(null);
    }
  };

  const confirmDelete = (row: DriverMedia) => {
    modal.confirm({
      title: tr("删除媒体包 {{arg0}}/{{arg1}}？", { arg0: row.mediaKey, arg1: mediaVersionLabel(row.version) }),
      content: tr("删除后引用该媒体版本的组合与下载将不可用；已落盘内容由孤儿清理任务回收。若该媒体键还有其它已启用版本不受影响。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => remove.mutateAsync(row.id),
    });
  };

  const columns: TableColumnsType<DriverMedia> = [
    {
      title: tr("媒体键"),
      dataIndex: 'mediaKey',
      key: 'mediaKey',
      width: 140,
      render: (key: string) => <Typography.Text code>{key}</Typography.Text>,
    },
    {
      title: tr("版本"),
      dataIndex: 'version',
      key: 'version',
      width: 110,
      render: (value: string) => <Typography.Text strong>{mediaVersionLabel(value)}</Typography.Text>,
    },
    { title: tr("名称"), dataIndex: 'name', key: 'name', width: 160, responsive: ['md'] },
    {
      title: tr("文件"),
      dataIndex: 'fileName',
      key: 'fileName',
      ellipsis: true,
      render: (fileName: string) => (
        <Typography.Text copyable={{ text: fileName }}>{fileName}</Typography.Text>
      ),
    },
    {
      title: tr("大小"),
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      responsive: ['md'],
      render: (size: number) => formatBytes(size),
    },
    { title: tr("文件数"), dataIndex: 'fileCount', key: 'fileCount', width: 80, responsive: ['md'] },
    {
      title: tr("安装顺序"),
      dataIndex: 'installOrder',
      key: 'installOrder',
      width: 160,
      responsive: ['md'],
      render: (order: string[]) =>
        order.length > 0 ? (
          <Tooltip title={order.join(' → ')}>
            <Space size={4}>
              <Tag>{order[0]}</Tag>
              {order.length > 1 ? <Tag>+{order.length - 1}</Tag> : null}
            </Space>
          </Tooltip>
        ) : (
          <Typography.Text type="secondary">-</Typography.Text>
        ),
    },
    {
      title: tr("状态"),
      dataIndex: 'isActive',
      key: 'isActive',
      width: 90,
      render: (isActive: boolean) =>
        isActive ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>,
    },
    {
      title: tr("登记时间"),
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      responsive: ['md'],
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 200,
      fixed: 'right',
      render: (_: unknown, row) => (
        <Space size="small">
          {canUpload && !row.isActive ? (
            <Button
              type="link"
              size="small"
              loading={activate.isPending && activate.variables === row.id}
              onClick={() => activate.mutate(row.id)}
            >
              {tr("设为启用")}</Button>
          ) : null}
          {canDownload ? (
            <Button
              type="link"
              size="small"
              icon={<DownloadOutlined />}
              loading={downloadingId === row.id}
              onClick={() => void download(row)}
            >
              {tr("下载")}</Button>
          ) : null}
          {canArchive ? (
            <Button
              type="link"
              size="small"
              danger
              loading={remove.isPending && remove.variables === row.id}
              onClick={() => confirmDelete(row)}
            >
              {tr("删除")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Card
      size="small"
      title={tr("驱动媒体")}
      extra={
        canUpload ? (
          <Button icon={<CloudUploadOutlined />} onClick={() => setUploadOpen(true)}>
            {tr("上传媒体包")}</Button>
        ) : null
      }
    >
      <Space wrap style={{ marginBottom: 12 }}>
        <Input
          allowClear
          placeholder={tr("按媒体键关键词过滤")}
          style={{ width: 200, maxWidth: '100%' }}
          value={mediaKeyFilter}
          onChange={(event) => {
            setMediaKeyFilter(event.target.value);
            setPage(1);
          }}
        />
        <Space size={6}>
          <Switch
            size="small"
            checked={activeOnly}
            onChange={(checked) => {
              setActiveOnly(checked);
              setPage(1);
            }}
          />
          <Typography.Text type="secondary">{tr("仅启用版本")}</Typography.Text>
        </Space>
        <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
          {tr("刷新")}</Button>
      </Space>

      {media.isError ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message={(media.error as Error).message}
          action={
            <Button size="small" onClick={() => void media.refetch()}>
              {tr("重试")}</Button>
          }
        />
      ) : null}
      <Table<DriverMedia>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={media.isPending}
        dataSource={media.data?.items ?? []}
        columns={columns}
        pagination={responsivePagination(isMobile, {
          current: media.data?.page ?? page,
          pageSize: media.data?.pageSize ?? pageSize,
          total: media.data?.total ?? 0,
          showTotal: (total: number) => tr("共 {{arg0}} 个媒体包", { arg0: total }),
          onChange: (nextPage: number, nextPageSize: number) => {
            setPage(nextPageSize === pageSize ? nextPage : 1);
            setPageSize(nextPageSize);
          },
        })}
        locale={{ emptyText: tr("尚未登记驱动媒体包；可用 -Kind media 直传或在此上传 media-<组件>.zip") }}
      />

      <MediaUploadModal
        open={uploadOpen}
        onClose={() => setUploadOpen(false)}
        onSaved={() => void refresh()}
      />
    </Card>
  );
}
