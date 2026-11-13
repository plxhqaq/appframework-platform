import {
  CloudUploadOutlined,
  DownloadOutlined,
  ReloadOutlined,
  RocketOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Empty,
  Select,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import { useState } from 'react';
import { ApiError, requestBlob } from '../../shared/api/client';
import { versionsApi } from '../../shared/api/versions';
import type { UpdaterInfo } from '../../shared/api/types';
import { saveBlob } from '../../shared/download';
import { formatBytes } from '../../shared/format';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';
import { safeDownloadName } from '../versions/packageUi';
import { UpdaterUploadModal } from './UpdaterUploadModal';

const RUNTIMES = ['any', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'];

const STATUS_META: Record<string, { label: string; color?: string }> = {
  published: { label: '已发布', color: 'green' },
  archived: { label: '已归档', color: 'orange' },
};

/**
 * 更新器版本线。
 *
 * 「最新」标记不用前端比版本号：逐个 target 调客户端实际使用的
 * `/client/versions/updater`，标出来的就是 check 真正会下发的那一版
 * （含 any 兜底语义），避免两端口径漂移。
 */
export function UpdatersPage() {
  const { message, modal } = App.useApp();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const switchAppContext = useAuthStore((state) => state.switchAppContext);
  const switching = useAuthStore((state) => state.pending);
  const canUpload = useAuthStore((state) => state.hasPermission(PERMISSIONS.versionsArtifactUpload));
  const canArchive = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.versionsReleaseArchive),
  );
  const canDownload = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.versionsArtifactDownload),
  );

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [uploadOpen, setUploadOpen] = useState(false);
  const [targetFilter, setTargetFilter] = useState<string | undefined>();
  const [statusFilter, setStatusFilter] = useState<string | undefined>();
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const updaters = useQuery({
    queryKey: ['versions', 'updaters', appKey, targetFilter, statusFilter],
    queryFn: async ({ signal }) => {
      const page = await versionsApi.listUpdaters(
        { pageSize: 100, target: targetFilter, status: statusFilter },
        signal,
      );

      const targets = Array.from(new Set(page.items.map((item) => item.target)));
      const latestIds = await Promise.all(
        targets.map(async (target) => {
          try {
            return (await versionsApi.latestUpdater(target, signal)).id;
          } catch (error) {
            // 该 target 还没有已发布更新器（含 any 兜底）时是 404，不算错误
            if (error instanceof ApiError && error.status === 404) {
              return null;
            }
            throw error;
          }
        }),
      );

      return {
        items: page.items,
        latestIds: new Set(latestIds.filter((id): id is string => id !== null)),
      };
    },
    enabled: appKey !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['versions', 'updaters'] });
  };

  const handleSwitchContext = async (next: string) => {
    if (next === appKey) {
      return;
    }

    try {
      const updated = await switchAppContext(next);
      message.success(`已进入应用 ${updated.appKey ?? next}`);
      setTargetFilter(undefined);
      setStatusFilter(undefined);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '切换应用失败');
    }
  };

  const archive = useMutation({
    mutationFn: (id: string) => versionsApi.archiveUpdater(id),
    onSuccess: async (archived) => {
      message.success(`更新器 ${archived.target}/${archived.version} 已归档`);
      await refresh();
    },
    onError: (error: Error) => message.error(error.message),
  });

  const confirmArchive = (row: UpdaterInfo) => {
    modal.confirm({
      title: `归档更新器 ${row.target}/${row.version}？`,
      content: '归档后 check 会回退到同目标上一版已发布更新器；下载入口同时关闭。',
      okText: '归档',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => archive.mutateAsync(row.id),
    });
  };

  const download = async (row: UpdaterInfo) => {
    setDownloadingId(row.id);
    try {
      const blob = await requestBlob(`/api/v1/client/versions/updaters/${row.id}/download`);
      saveBlob(blob, safeDownloadName(row.fileName));
    } catch (error) {
      message.error(error instanceof Error ? error.message : '下载失败');
    } finally {
      setDownloadingId(null);
    }
  };

  const latestIds = updaters.data?.latestIds ?? new Set<string>();

  const columns = [
    {
      title: '版本',
      dataIndex: 'version',
      key: 'version',
      width: 150,
      render: (version: string, row: UpdaterInfo) => (
        <Space size={4}>
          <Typography.Text strong>{version}</Typography.Text>
          {latestIds.has(row.id) ? <Tag color="blue">最新</Tag> : null}
        </Space>
      ),
    },
    {
      title: '目标',
      dataIndex: 'target',
      key: 'target',
      width: 120,
      render: (target: string) => <Typography.Text code>{target}</Typography.Text>,
    },
    {
      title: '文件',
      dataIndex: 'fileName',
      key: 'fileName',
      ellipsis: true,
      render: (fileName: string) => (
        <Typography.Text copyable={{ text: fileName }}>{fileName}</Typography.Text>
      ),
    },
    {
      title: '大小',
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      render: (size: number) => formatBytes(size),
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 90,
      render: (status: string) => {
        const meta = STATUS_META[status] ?? { label: status };
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: '清单哈希',
      dataIndex: 'manifestHash',
      key: 'manifestHash',
      width: 130,
      render: (hash: string | null | undefined) =>
        hash ? (
          <Typography.Text type="secondary" copyable={{ text: hash }}>
            {hash.slice(0, 10)}…
          </Typography.Text>
        ) : (
          <Typography.Text type="secondary">-</Typography.Text>
        ),
    },
    {
      title: '发布时间',
      dataIndex: 'publishedAt',
      key: 'publishedAt',
      width: 170,
      render: (value: string | null | undefined) =>
        value ? new Date(value).toLocaleString() : '-',
    },
    {
      title: '操作',
      key: 'actions',
      width: 160,
      render: (_: unknown, row: UpdaterInfo) => (
        <Space size="small">
          {canDownload && row.status === 'published' ? (
            <Button
              type="link"
              size="small"
              icon={<DownloadOutlined />}
              loading={downloadingId === row.id}
              onClick={() => void download(row)}
            >
              下载
            </Button>
          ) : null}
          {canArchive && row.status === 'published' ? (
            <Button
              type="link"
              size="small"
              danger
              loading={archive.isPending}
              onClick={() => confirmArchive(row)}
            >
              归档
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title="更新器" size="small">
        <Alert
          type="warning"
          showIcon
          message="当前账号没有可用的应用上下文"
          description="更新器按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。"
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card size="small">
        <Space wrap>
          <RocketOutlined />
          <Typography.Text>当前应用</Typography.Text>
          <Select
            value={appKey}
            loading={switching}
            style={{ minWidth: 200 }}
            options={appKeys.map((key) => ({ value: key, label: key }))}
            onChange={(value) => void handleSwitchContext(value)}
            disabled={switching || appKeys.length <= 1}
          />
          <Tooltip title="更新器版本线独立于应用版本；客户端 check 会同时返回最新更新器。">
            <Typography.Text type="secondary">同 target + version 只允许一份内容。</Typography.Text>
          </Tooltip>
        </Space>
      </Card>

      <Card
        size="small"
        title="更新器版本"
        extra={
          canUpload ? (
            <Button
              type="primary"
              icon={<CloudUploadOutlined />}
              onClick={() => setUploadOpen(true)}
            >
              上传更新器
            </Button>
          ) : null
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Select
            allowClear
            placeholder="全部目标"
            style={{ width: 180 }}
            value={targetFilter}
            onChange={setTargetFilter}
            options={RUNTIMES.map((value) => ({ value, label: value }))}
          />
          <Select
            allowClear
            placeholder="全部状态"
            style={{ width: 160 }}
            value={statusFilter}
            onChange={setStatusFilter}
            options={[
              { value: 'published', label: '已发布' },
              { value: 'archived', label: '已归档' },
            ]}
          />
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            刷新
          </Button>
        </Space>

        {updaters.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(updaters.error as Error).message}
          />
        ) : null}

        <Table<UpdaterInfo>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={updaters.isPending}
          dataSource={updaters.data?.items ?? []}
          columns={columns}
          pagination={false}
          locale={{ emptyText: <Empty description="尚未登记更新器；客户端会跳过更新器升级" /> }}
        />
      </Card>

      <UpdaterUploadModal
        open={uploadOpen}
        onClose={() => setUploadOpen(false)}
        onSaved={() => void refresh()}
      />
    </Space>
  );
}
