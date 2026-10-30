import { AppstoreOutlined, PlusOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Form,
  Input,
  InputNumber,
  Modal,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import { useState } from 'react';
import { backupsApi } from '../../shared/api/backups';
import { requestBlob } from '../../shared/api/client';
import type {
  BackupKind,
  BackupNamespace,
  BackupSummary,
  CreateBackupNamespaceRequest,
  PushBackupRequest,
  UpdateBackupNamespaceRequest,
} from '../../shared/api/types';
import { saveBlob } from '../../shared/download';
import { formatBytes } from '../../shared/format';
import { PERMISSIONS } from '../../shared/permissions';
import { useAuthStore } from '../../stores/auth';

interface NamespaceFormValues {
  key: string;
  name: string;
  description?: string;
  channel: string;
  maxVersions: number;
  isEnabled: boolean;
}

interface BackupFormValues {
  namespaceKey: string;
  kind: BackupKind;
  name: string;
  frameworkVersion: string;
  schemaVersion: number;
  deviceName?: string;
  payloadJson: string;
}

interface PushFormValues {
  targetChannel?: string;
  targetFrameworkVersion?: string;
  force: boolean;
  currentHash?: string;
  confirmCrossDevice: boolean;
}

/**
 * 配置备份页。
 *
 * 服务端按 64KB 阈值决定内联或转存；下发只是提示，客户端回拉下载后回执。
 * 渠道是硬门禁：即使勾选 force 也不允许跨渠道下发。
 */
export function BackupsPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const switchAppContext = useAuthStore((state) => state.switchAppContext);
  const switching = useAuthStore((state) => state.pending);
  const canManageNamespace = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.backupNamespaceManage),
  );
  const canCreate = useAuthStore((state) => state.hasPermission(PERMISSIONS.backupCreate));
  const canPush = useAuthStore((state) => state.hasPermission(PERMISSIONS.backupPush));
  const canDelete = useAuthStore((state) => state.hasPermission(PERMISSIONS.backupDelete));

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [namespaceForm] = Form.useForm<NamespaceFormValues>();
  const [backupForm] = Form.useForm<BackupFormValues>();
  const [pushForm] = Form.useForm<PushFormValues>();

  const [namespaceOpen, setNamespaceOpen] = useState(false);
  const [editingNamespace, setEditingNamespace] = useState<BackupNamespace | null>(null);
  const [backupOpen, setBackupOpen] = useState(false);
  const [pushTarget, setPushTarget] = useState<BackupSummary | null>(null);
  const [namespaceFilter, setNamespaceFilter] = useState<string | undefined>();
  const [saving, setSaving] = useState(false);

  const namespaces = useQuery({
    queryKey: ['backups', 'namespaces', appKey],
    queryFn: ({ signal }) => backupsApi.listNamespaces(signal),
    enabled: appKey !== null,
  });

  const backups = useQuery({
    queryKey: ['backups', 'list', appKey, namespaceFilter],
    queryFn: ({ signal }) =>
      backupsApi.listBackups({ namespaceKey: namespaceFilter, pageSize: 50 }, signal),
    enabled: appKey !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['backups'] });
  };

  const handleSwitchContext = async (next: string) => {
    if (next === appKey) {
      return;
    }

    try {
      const updated = await switchAppContext(next);
      message.success(`已进入应用 ${updated.appKey ?? next}`);
      setNamespaceFilter(undefined);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '切换应用失败');
    }
  };

  const openNamespaceForm = (namespace: BackupNamespace | null) => {
    setEditingNamespace(namespace);
    namespaceForm.resetFields();
    namespaceForm.setFieldsValue({
      key: namespace?.key ?? '',
      name: namespace?.name ?? '',
      description: namespace?.description ?? '',
      channel: namespace?.channel ?? 'default',
      maxVersions: namespace?.maxVersions ?? 50,
      isEnabled: namespace?.isEnabled ?? true,
    });
    setNamespaceOpen(true);
  };

  const submitNamespace = async () => {
    const values = await namespaceForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    setSaving(true);
    try {
      if (editingNamespace) {
        const body: UpdateBackupNamespaceRequest = {
          name: values.name,
          description: values.description ?? null,
          channel: values.channel,
          maxVersions: values.maxVersions,
          isEnabled: values.isEnabled,
        };
        await backupsApi.updateNamespace(editingNamespace.id, body);
        message.success('命名空间已更新');
      } else {
        const body: CreateBackupNamespaceRequest = {
          key: values.key,
          name: values.name,
          description: values.description ?? null,
          channel: values.channel,
          maxVersions: values.maxVersions,
        };
        await backupsApi.createNamespace(body);
        message.success('命名空间已创建');
      }

      setNamespaceOpen(false);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '保存失败');
    } finally {
      setSaving(false);
    }
  };

  const openBackupForm = () => {
    const first = (namespaces.data ?? []).find((item) => item.isEnabled);
    backupForm.resetFields();
    backupForm.setFieldsValue({
      namespaceKey: first?.key ?? '',
      kind: 'config',
      name: '',
      frameworkVersion: '1.0.0',
      schemaVersion: 1,
      deviceName: '',
      payloadJson: '{}',
    });
    setBackupOpen(true);
  };

  const submitBackup = async () => {
    const values = await backupForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    setSaving(true);
    try {
      await backupsApi.createBackup({
        namespaceKey: values.namespaceKey,
        kind: values.kind,
        name: values.name,
        frameworkVersion: values.frameworkVersion,
        schemaVersion: values.schemaVersion,
        deviceName: values.deviceName || null,
        payloadJson: values.payloadJson,
      });
      message.success('备份已创建');
      setBackupOpen(false);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '创建备份失败');
    } finally {
      setSaving(false);
    }
  };

  const openPushForm = (row: BackupSummary) => {
    setPushTarget(row);
    pushForm.resetFields();
    pushForm.setFieldsValue({
      targetChannel: row.channel,
      targetFrameworkVersion: row.frameworkVersion,
      force: false,
      currentHash: '',
      confirmCrossDevice: false,
    });
  };

  const submitPush = async () => {
    if (!pushTarget) {
      return;
    }

    const values = await pushForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    setSaving(true);
    try {
      const body: PushBackupRequest = {
        targetDeviceId: null,
        targetChannel: values.targetChannel ?? null,
        targetFrameworkVersion: values.targetFrameworkVersion ?? null,
        restartAfterApply: false,
        force: values.force,
        currentHash: values.currentHash?.trim() || null,
        confirmCrossDevice: values.confirmCrossDevice,
      };
      const result = await backupsApi.pushBackup(pushTarget.id, body);
      if (result.success) {
        message.success(result.message ?? '目标已回执并应用');
      } else {
        message.warning(result.message ?? '目标回执失败');
      }
      setPushTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : '下发失败');
    } finally {
      setSaving(false);
    }
  };

  const handleDownload = async (row: BackupSummary) => {
    try {
      const blob = await requestBlob(row.downloadUrl ?? `/api/v1/admin/backups/${row.id}/download`);
      saveBlob(blob, row.fileName ?? `${row.name}.json`);
    } catch (error) {
      message.error(error instanceof Error ? error.message : '下载失败');
    }
  };

  const handleDelete = async (row: BackupSummary) => {
    Modal.confirm({
      title: `删除备份「${row.name}」？`,
      content: '删除后客户端不可再下发；物理文件在宽限期后由后台清理。',
      okText: '删除',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: async () => {
        try {
          await backupsApi.deleteBackup(row.id);
          message.success('备份已删除');
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : '删除失败');
        }
      },
    });
  };

  const namespaceColumns = [
    {
      title: '键',
      dataIndex: 'key',
      key: 'key',
      width: 140,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    { title: '名称', dataIndex: 'name', key: 'name', width: 160 },
    { title: '渠道', dataIndex: 'channel', key: 'channel', width: 110 },
    { title: '保留上限', dataIndex: 'maxVersions', key: 'maxVersions', width: 90 },
    { title: '备份数', dataIndex: 'backupCount', key: 'backupCount', width: 80 },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 80,
      render: (_: unknown, row: BackupNamespace) =>
        canManageNamespace ? (
          <Button type="link" size="small" onClick={() => openNamespaceForm(row)}>
            编辑
          </Button>
        ) : null,
    },
  ];

  const backupColumns = [
    { title: '命名空间', dataIndex: 'namespaceKey', key: 'namespaceKey', width: 130 },
    { title: '名称', dataIndex: 'name', key: 'name', width: 180 },
    {
      title: '类型',
      dataIndex: 'kind',
      key: 'kind',
      width: 80,
      render: (kind: BackupKind) => <Tag>{kind}</Tag>,
    },
    { title: '渠道', dataIndex: 'channel', key: 'channel', width: 100 },
    { title: '框架版本', dataIndex: 'frameworkVersion', key: 'frameworkVersion', width: 100 },
    {
      title: '存储',
      key: 'storage',
      width: 90,
      render: (_: unknown, row: BackupSummary) =>
        row.hasInline ? <Tag color="blue">内联</Tag> : <Tag color="purple">文件</Tag>,
    },
    {
      title: '大小',
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      render: (value: number) => formatBytes(value),
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      render: (value: string) => new Date(value).toLocaleString(),
    },
    {
      title: '操作',
      key: 'actions',
      width: 200,
      render: (_: unknown, row: BackupSummary) => (
        <Space size="small">
          <Button type="link" size="small" onClick={() => void handleDownload(row)}>
            下载
          </Button>
          {canPush ? (
            <Button type="link" size="small" onClick={() => openPushForm(row)}>
              下发
            </Button>
          ) : null}
          {canDelete ? (
            <Button type="link" size="small" danger onClick={() => void handleDelete(row)}>
              删除
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title="配置备份" size="small">
        <Alert
          type="warning"
          showIcon
          message="当前账号没有可用的应用上下文"
          description="备份按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。"
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card size="small">
        <Space wrap>
          <AppstoreOutlined />
          <Typography.Text>当前应用</Typography.Text>
          <Select
            value={appKey}
            loading={switching}
            style={{ minWidth: 200 }}
            options={appKeys.map((key) => ({ value: key, label: key }))}
            onChange={(value) => void handleSwitchContext(value)}
            disabled={switching || appKeys.length <= 1}
          />
          <Tooltip title="应用上下文来自令牌；备份、文档、版本等应用级数据随上下文切换。">
            <Typography.Text type="secondary">备份按应用隔离，前端不传 appKey。</Typography.Text>
          </Tooltip>
        </Space>
      </Card>

      <Card
        size="small"
        title="命名空间"
        extra={
          canManageNamespace ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => openNamespaceForm(null)}
            >
              新建命名空间
            </Button>
          ) : null
        }
      >
        {namespaces.isError ? (
          <Alert type="error" showIcon style={{ marginBottom: 16 }} message={(namespaces.error as Error).message} />
        ) : null}
        <Table<BackupNamespace>
          rowKey="id"
          size="small"
          loading={namespaces.isPending}
          dataSource={namespaces.data ?? []}
          columns={namespaceColumns}
          pagination={false}
          locale={{ emptyText: '尚未创建命名空间（如 app-settings / global）' }}
        />
      </Card>

      <Card
        size="small"
        title="备份"
        extra={
          canCreate ? (
            <Button type="primary" icon={<PlusOutlined />} onClick={openBackupForm}>
              新建备份
            </Button>
          ) : null
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Select
            allowClear
            placeholder="全部命名空间"
            style={{ width: 200 }}
            value={namespaceFilter}
            onChange={setNamespaceFilter}
            options={(namespaces.data ?? []).map((item) => ({
              value: item.key,
              label: `${item.name}（${item.key}）`,
            }))}
          />
          <Button onClick={() => void refresh()}>刷新</Button>
        </Space>

        {backups.isError ? (
          <Alert type="error" showIcon style={{ marginBottom: 16 }} message={(backups.error as Error).message} />
        ) : null}
        <Table<BackupSummary>
          rowKey="id"
          size="small"
          loading={backups.isPending}
          dataSource={backups.data?.items ?? []}
          columns={backupColumns}
          pagination={false}
          locale={{ emptyText: '尚无备份' }}
        />
      </Card>

      <Modal
        open={namespaceOpen}
        title={editingNamespace ? '编辑命名空间' : '新建命名空间'}
        confirmLoading={saving}
        onOk={() => void submitNamespace()}
        onCancel={() => setNamespaceOpen(false)}
      >
        <Form form={namespaceForm} layout="vertical">
          <Form.Item
            name="key"
            label="键"
            rules={[{ required: true, pattern: /^[a-z][a-z0-9-]{1,31}$/, message: '小写字母开头，2–32 位小写字母/数字/连字符' }]}
          >
            <Input disabled={editingNamespace !== null} placeholder="app-settings" />
          </Form.Item>
          <Form.Item name="name" label="名称" rules={[{ required: true, max: 256 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="description" label="说明">
            <Input.TextArea rows={2} />
          </Form.Item>
          <Form.Item name="channel" label="兼容渠道（硬门禁）" rules={[{ required: true }]}>
            <Input placeholder="default" />
          </Form.Item>
          <Form.Item name="maxVersions" label="保留上限" rules={[{ required: true }]}>
            <InputNumber min={1} max={500} style={{ width: '100%' }} />
          </Form.Item>
          {editingNamespace ? (
            <Form.Item name="isEnabled" label="启用" valuePropName="checked">
              <Switch />
            </Form.Item>
          ) : null}
        </Form>
      </Modal>

      <Modal
        open={backupOpen}
        title="新建备份"
        confirmLoading={saving}
        onOk={() => void submitBackup()}
        onCancel={() => setBackupOpen(false)}
        width={640}
      >
        <Form form={backupForm} layout="vertical">
          <Form.Item name="namespaceKey" label="命名空间" rules={[{ required: true }]}>
            <Select
              options={(namespaces.data ?? [])
                .filter((item) => item.isEnabled)
                .map((item) => ({ value: item.key, label: `${item.name}（${item.key}）` }))}
            />
          </Form.Item>
          <Form.Item name="kind" label="类型" rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'config', label: '配置 config' },
                { value: 'profile', label: '档案 profile' },
              ]}
            />
          </Form.Item>
          <Form.Item name="name" label="名称" rules={[{ required: true, max: 256 }]}>
            <Input />
          </Form.Item>
          <Space size={12} style={{ display: 'flex' }}>
            <Form.Item name="frameworkVersion" label="框架版本" rules={[{ required: true }]} style={{ flex: 1 }}>
              <Input placeholder="1.0.0" />
            </Form.Item>
            <Form.Item name="schemaVersion" label="Schema 版本" rules={[{ required: true }]} style={{ flex: 1 }}>
              <InputNumber min={0} style={{ width: '100%' }} />
            </Form.Item>
          </Space>
          <Form.Item name="deviceName" label="来源设备名（可选）">
            <Input />
          </Form.Item>
          <Form.Item name="payloadJson" label="负载 JSON" rules={[{ required: true }]}>
            <Input.TextArea rows={10} spellCheck={false} />
          </Form.Item>
          <Typography.Text type="secondary">
            不超过 64KB 直接内联；超过则转存文件（仍按内容哈希校验）。
          </Typography.Text>
        </Form>
      </Modal>

      <Modal
        open={pushTarget !== null}
        title={pushTarget ? `下发备份：${pushTarget.name}` : '下发备份'}
        confirmLoading={saving}
        onOk={() => void submitPush()}
        onCancel={() => setPushTarget(null)}
      >
        <Form form={pushForm} layout="vertical">
          <Form.Item name="targetChannel" label="目标渠道">
            <Input placeholder={pushTarget?.channel ?? 'default'} />
          </Form.Item>
          <Form.Item name="targetFrameworkVersion" label="目标框架版本">
            <Input placeholder={pushTarget?.frameworkVersion ?? '1.0.0'} />
          </Form.Item>
          <Form.Item name="currentHash" label="目标当前配置哈希（可选，提供时先落快照）">
            <Input placeholder="64 位十六进制" />
          </Form.Item>
          <Form.Item name="force" label="放宽主版本校验（渠道不一致仍拒绝）" valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="confirmCrossDevice" label="确认跨设备下发" valuePropName="checked">
            <Switch />
          </Form.Item>
          <Typography.Text type="secondary">
            下发只是提示：目标客户端回拉下载并应用后回执，超时视为失败。
          </Typography.Text>
        </Form>
      </Modal>
    </Space>
  );
}
