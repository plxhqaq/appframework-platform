import { tr } from '../../i18n/translate';
import { PlusOutlined, ReloadOutlined, SelectOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Flex,
  Form,
  Input,
  InputNumber,
  Modal,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { backupsApi } from '../../shared/api/backups';
import { requestBlob } from '../../shared/api/client';
import { installationLabel, installationsApi } from '../../shared/api/installations';
import type {
  BackupKind,
  BackupNamespace,
  BackupSummary,
  CreateBackupNamespaceRequest,
  InstallationSummary,
  PushBackupRequest,
  PushBackupResult,
  PushTargetResult,
  UpdateBackupNamespaceRequest,
} from '../../shared/api/types';
import { saveBlob } from '../../shared/download';
import { formatBytes, formatDateTime } from '../../shared/format';
import { PERMISSIONS } from '../../shared/permissions';
import { responsivePagination, responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { InstallationPickerModal } from '../../shared/ui/InstallationPickerModal';
import { isInstallationSelectable } from '../../shared/installationSelection';
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
  payloadJson: string;
}

interface PushFormValues {
  targetChannel?: string;
  targetFrameworkVersion?: string;
  force: boolean;
  currentHash?: string;
  confirmCrossInstallation: boolean;
}

/**
 * 配置备份页。
 *
 * 服务端按 64KB 阈值决定内联或转存；下发只是提示，客户端回拉下载后回执。
 * 渠道是硬门禁：即使勾选 force 也不允许跨渠道下发。
 */
export function BackupsPage() {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
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
  const [sourceInstallation, setSourceInstallation] = useState<InstallationSummary | null>(null);
  const [sourcePickerOpen, setSourcePickerOpen] = useState(false);
  const [selectedInstallations, setSelectedInstallations] = useState<
    Map<string, InstallationSummary>
  >(new Map());
  const [pickerOpen, setPickerOpen] = useState(false);
  const [sourceInstallationHint, setSourceInstallationHint] = useState<string | null>(null);
  const [pushResult, setPushResult] = useState<{
    result: PushBackupResult;
    installations: Map<string, InstallationSummary>;
  } | null>(null);
  const pushOpenToken = useRef(0);
  const [namespaceFilter, setNamespaceFilter] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [saving, setSaving] = useState(false);

  // 上下文在 Header 切换；旧应用的命名空间筛选不能带到新应用
  useEffect(() => {
    setNamespaceFilter(undefined);
    setPage(1);
  }, [appKey]);

  const namespaces = useQuery({
    queryKey: ['backups', 'namespaces', appKey],
    queryFn: ({ signal }) => backupsApi.listNamespaces(signal),
    enabled: appKey !== null,
  });

  const backups = useQuery({
    queryKey: ['backups', 'list', appKey, namespaceFilter, page, pageSize],
    queryFn: ({ signal }) =>
      backupsApi.listBackups({ namespaceKey: namespaceFilter, page, pageSize }, signal),
    enabled: appKey !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['backups'] });
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
        message.success(tr("命名空间已更新"));
      } else {
        const body: CreateBackupNamespaceRequest = {
          key: values.key,
          name: values.name,
          description: values.description ?? null,
          channel: values.channel,
          maxVersions: values.maxVersions,
        };
        await backupsApi.createNamespace(body);
        message.success(tr("命名空间已创建"));
      }

      setNamespaceOpen(false);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
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
      payloadJson: '{}',
    });
    setSourceInstallation(null);
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
        installationId: sourceInstallation?.id ?? null,
        payloadJson: values.payloadJson,
      });
      message.success(tr("备份已创建"));
      setBackupOpen(false);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("创建备份失败"));
    } finally {
      setSaving(false);
    }
  };

  const openPushForm = (row: BackupSummary) => {
    const token = ++pushOpenToken.current;
    setPushTarget(row);
    setSelectedInstallations(new Map());
    setSourceInstallationHint(null);
    setPickerOpen(false);
    pushForm.resetFields();
    pushForm.setFieldsValue({
      targetChannel: row.channel,
      targetFrameworkVersion: row.frameworkVersion,
      force: false,
      currentHash: '',
      confirmCrossInstallation: false,
    });

    // 默认选中备份登记时的来源安装实例；手动添加的备份没有来源，保持留空。
    if (row.installationId) {
      installationsApi
        .getInstallation(row.installationId)
        .then((installation) => {
          if (token !== pushOpenToken.current) {
            return;
          }

          if (isInstallationSelectable(installation)) {
            setSelectedInstallations(new Map([[installation.id, installation]]));
          } else {
            setSourceInstallationHint(
              tr("来源安装实例「{{arg0}}」当前不可下发（需启用且在线待机），未默认选中。", {
                arg0: installationLabel(installation),
              }),
            );
          }
        })
        .catch(() => {
          if (token === pushOpenToken.current) {
            setSourceInstallationHint(tr("备份来源安装实例不存在或已删除，未默认选中。"));
          }
        });
    }
  };

  const closePushForm = () => {
    pushOpenToken.current += 1;
    setPushTarget(null);
    setPickerOpen(false);
  };

  const removeSelectedInstallation = (installationId: string) => {
    setSelectedInstallations((previous) => {
      const next = new Map(previous);
      next.delete(installationId);
      return next;
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

    const targets = [...selectedInstallations.values()];
    if (targets.length === 0) {
      message.warning(tr("请选择至少一个目标安装实例"));
      return;
    }

    const unavailable = targets.find((installation) => !isInstallationSelectable(installation));
    if (unavailable) {
      message.warning(
        tr("安装实例「{{arg0}}」当前不可下发（需启用且在线待机）", {
          arg0: installationLabel(unavailable),
        }),
      );
      return;
    }

    if (
      pushTarget.installationId &&
      targets.some((installation) => installation.id !== pushTarget.installationId) &&
      !values.confirmCrossInstallation
    ) {
      message.warning(tr("这是跨安装实例下发，请勾选「确认跨安装实例下发」"));
      return;
    }

    if (targets.length > 1 && values.currentHash?.trim()) {
      message.warning(tr("多安装实例下发不支持目标当前配置哈希，请改用单安装实例下发"));
      return;
    }

    setSaving(true);
    try {
      const body: PushBackupRequest = {
        targetInstallationId: null,
        targetInstallationIds: targets.map((installation) => installation.id),
        broadcastToApp: false,
        targetChannel: values.targetChannel ?? null,
        targetFrameworkVersion: values.targetFrameworkVersion ?? null,
        restartAfterApply: false,
        force: values.force,
        currentHash: targets.length === 1 ? values.currentHash?.trim() || null : null,
        confirmCrossInstallation: values.confirmCrossInstallation,
      };
      const result = await backupsApi.pushBackup(pushTarget.id, body);
      setPushResult({ result, installations: selectedInstallations });
      closePushForm();
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("下发失败"));
    } finally {
      setSaving(false);
    }
  };

  const handleDownload = async (row: BackupSummary) => {
    try {
      const blob = await requestBlob(row.downloadUrl ?? `/api/v1/admin/backups/${row.id}/download`);
      saveBlob(blob, row.fileName ?? `${row.name}.json`);
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("下载失败"));
    }
  };

  const handleDelete = async (row: BackupSummary) => {
    Modal.confirm({
      title: tr("删除备份「{{arg0}}」？", { arg0: row.name }),
      content: tr("删除后客户端不可再下发；物理文件在宽限期后由后台清理。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          await backupsApi.deleteBackup(row.id);
          message.success(tr("备份已删除"));
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("删除失败"));
        }
      },
    });
  };

  const namespaceColumns: TableColumnsType<BackupNamespace> = [
    {
      title: tr("键"),
      dataIndex: 'key',
      key: 'key',
      width: 140,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    { title: tr("名称"), dataIndex: 'name', key: 'name', width: 160 },
    { title: tr("渠道"), dataIndex: 'channel', key: 'channel', width: 110, responsive: ['md'] },
    {
      title: tr("保留上限"),
      dataIndex: 'maxVersions',
      key: 'maxVersions',
      width: 90,
      responsive: ['md'],
    },
    {
      title: tr("备份数"),
      dataIndex: 'backupCount',
      key: 'backupCount',
      width: 80,
      responsive: ['md'],
    },
    {
      title: tr("状态"),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 130,
      fixed: 'right',
      render: (_: unknown, row: BackupNamespace) =>
        canManageNamespace ? (
          <Space size="small">
            <Button type="link" size="small" onClick={() => openNamespaceForm(row)}>
              {tr("编辑")}
            </Button>
            <Button type="link" size="small" danger onClick={() => confirmDeleteNamespace(row)}>
              {tr("删除")}
            </Button>
          </Space>
        ) : null,
    },
  ];

  const deleteNamespaceRow = async (namespace: BackupNamespace) => {
    try {
      await backupsApi.deleteNamespace(namespace.id);
      message.success(tr("命名空间 {{arg0}} 已删除", { arg0: namespace.key }));
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("操作失败"));
    }
  };

  const confirmDeleteNamespace = (namespace: BackupNamespace) => {
    modal.confirm({
      title: tr("删除备份命名空间 {{arg0}}？", { arg0: namespace.key }),
      content: tr("命名空间内仍有备份（含软删除待清理）时会被拒绝，以保留备份留痕。此操作不可恢复。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => deleteNamespaceRow(namespace),
    });
  };

  const backupColumns: TableColumnsType<BackupSummary> = [
    { title: tr("命名空间"), dataIndex: 'namespaceKey', key: 'namespaceKey', width: 130 },
    { title: tr("名称"), dataIndex: 'name', key: 'name', width: 180 },
    {
      title: tr("类型"),
      dataIndex: 'kind',
      key: 'kind',
      width: 80,
      render: (kind: BackupKind) => <Tag>{kind}</Tag>,
    },
    {
      title: tr("渠道"),
      dataIndex: 'channel',
      key: 'channel',
      width: 100,
      responsive: ['md'],
    },
    {
      title: tr("来源节点"),
      dataIndex: 'nodeName',
      key: 'nodeName',
      width: 160,
      responsive: ['md'],
      render: (value: string | null) => value ?? '-',
    },
    {
      title: tr("框架版本"),
      dataIndex: 'frameworkVersion',
      key: 'frameworkVersion',
      width: 100,
      responsive: ['md'],
    },
    {
      title: tr("存储"),
      key: 'storage',
      width: 90,
      responsive: ['md'],
      render: (_: unknown, row: BackupSummary) =>
        row.hasInline ? <Tag color="blue">{tr("内联")}</Tag> : <Tag color="purple">{tr("文件")}</Tag>,
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
      title: tr("创建时间"),
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
      render: (_: unknown, row: BackupSummary) => (
        <Space size="small">
          <Button type="link" size="small" onClick={() => void handleDownload(row)}>
            {tr("下载")}</Button>
          {canPush ? (
            <Button type="link" size="small" onClick={() => openPushForm(row)}>
              {tr("下发")}</Button>
          ) : null}
          {canDelete ? (
            <Button type="link" size="small" danger onClick={() => void handleDelete(row)}>
              {tr("删除")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  const selectedList = [...selectedInstallations.values()];
  const needsCrossInstallationConfirm =
    pushTarget?.installationId != null
    && selectedList.some((installation) => installation.id !== pushTarget.installationId);

  const targetResultColumns: TableColumnsType<PushTargetResult> = [
    {
      title: tr("安装实例"),
      dataIndex: 'installationId',
      key: 'installationId',
      ellipsis: true,
      render: (installationId: string) => {
        const target = pushResult?.installations.get(installationId);
        return target ? installationLabel(target) : installationId;
      },
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 100,
      render: (_: unknown, row: PushTargetResult) => {
        if (!row.delivered) {
          return <Tag color="red">{tr("未送达")}</Tag>;
        }

        if (!row.acked) {
          return <Tag color="orange">{tr("未回执")}</Tag>;
        }

        return row.success ? <Tag color="green">{tr("成功")}</Tag> : <Tag color="red">{tr("失败")}</Tag>;
      },
    },
    {
      title: tr("说明"),
      dataIndex: 'message',
      key: 'message',
      render: (value: string | null) => value ?? '-',
    },
  ];

  if (appKey === null) {
    return (
      <Card title={tr("配置备份")} size="small">
        <Alert
          type="warning"
          showIcon
          message={tr("当前账号没有可用的应用上下文")}
          description={tr("备份按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。")}
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title={tr("命名空间")}
        extra={
          canManageNamespace ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => openNamespaceForm(null)}
            >
              {tr("新建命名空间")}</Button>
          ) : null
        }
      >
        {namespaces.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(namespaces.error as Error).message}
            action={
              <Button size="small" onClick={() => void namespaces.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<BackupNamespace>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={namespaces.isPending}
          dataSource={namespaces.data ?? []}
          columns={namespaceColumns}
          pagination={false}
          locale={{ emptyText: tr("尚未创建命名空间（如 app-settings / global）") }}
        />
      </Card>

      <Card
        size="small"
        title={tr("备份")}
        extra={
          canCreate ? (
            <Button type="primary" icon={<PlusOutlined />} onClick={openBackupForm}>
              {tr("新建备份")}</Button>
          ) : null
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Select
            allowClear
            placeholder={tr("全部命名空间")}
            style={{ width: 200, maxWidth: '100%' }}
            value={namespaceFilter}
            onChange={(value) => {
              setNamespaceFilter(value);
              setPage(1);
            }}
            options={(namespaces.data ?? []).map((item) => ({
              value: item.key,
              label: `${item.name}（${item.key}）`,
            }))}
          />
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            {tr("刷新")}</Button>
        </Space>

        {backups.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(backups.error as Error).message}
            action={
              <Button size="small" onClick={() => void backups.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<BackupSummary>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={backups.isPending}
          dataSource={backups.data?.items ?? []}
          columns={backupColumns}
          pagination={responsivePagination(isMobile, {
            current: backups.data?.page ?? page,
            pageSize: backups.data?.pageSize ?? pageSize,
            total: backups.data?.total ?? 0,
            showTotal: (total: number) => tr("共 {{arg0}} 份备份", { arg0: total }),
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: tr("尚无备份") }}
        />
      </Card>

      <Modal
        open={namespaceOpen}
        title={editingNamespace ? tr("编辑命名空间") : tr("新建命名空间")}
        confirmLoading={saving}
        onOk={() => void submitNamespace()}
        onCancel={() => setNamespaceOpen(false)}
      >
        <Form form={namespaceForm} layout="vertical">
          <Form.Item
            name="key"
            label={tr("键")}
            rules={[{ required: true, pattern: /^[a-z][a-z0-9-]{1,31}$/, message: tr("小写字母开头，2–32 位小写字母/数字/连字符") }]}
          >
            <Input disabled={editingNamespace !== null} placeholder="app-settings" />
          </Form.Item>
          <Form.Item name="name" label={tr("名称")} rules={[{ required: true, max: 256 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="description" label={tr("说明")}>
            <Input.TextArea rows={2} />
          </Form.Item>
          <Form.Item name="channel" label={tr("兼容渠道（硬门禁）")} rules={[{ required: true }]}>
            <Input placeholder="default" />
          </Form.Item>
          <Form.Item name="maxVersions" label={tr("保留上限")} rules={[{ required: true }]}>
            <InputNumber min={1} max={500} style={{ width: '100%' }} />
          </Form.Item>
          {editingNamespace ? (
            <Form.Item name="isEnabled" label={tr("启用")} valuePropName="checked">
              <Switch />
            </Form.Item>
          ) : null}
        </Form>
      </Modal>

      <Modal
        open={backupOpen}
        title={tr("新建备份")}
        confirmLoading={saving}
        onOk={() => void submitBackup()}
        onCancel={() => setBackupOpen(false)}
        width={responsiveWidth(isMobile, 640)}
      >
        <Form form={backupForm} layout="vertical">
          <Form.Item name="namespaceKey" label={tr("命名空间")} rules={[{ required: true }]}>
            <Select
              options={(namespaces.data ?? [])
                .filter((item) => item.isEnabled)
                .map((item) => ({ value: item.key, label: `${item.name}（${item.key}）` }))}
            />
          </Form.Item>
          <Form.Item name="kind" label={tr("类型")} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'config', label: tr("配置 config") },
                { value: 'profile', label: tr("档案 profile") },
              ]}
            />
          </Form.Item>
          <Form.Item name="name" label={tr("名称")} rules={[{ required: true, max: 256 }]}>
            <Input />
          </Form.Item>
          <Flex wrap gap={12} align="flex-start">
            <Form.Item
              name="frameworkVersion"
              label={tr("框架版本")}
              rules={[{ required: true }]}
              style={{ flex: '1 1 220px', minWidth: 0 }}
            >
              <Input placeholder="1.0.0" />
            </Form.Item>
            <Form.Item
              name="schemaVersion"
              label={tr("Schema 版本")}
              rules={[{ required: true }]}
              style={{ flex: '1 1 220px', minWidth: 0 }}
            >
              <InputNumber min={0} style={{ width: '100%' }} />
            </Form.Item>
          </Flex>
          <Form.Item label={tr("来源安装实例（可选，仅用于登记来源节点）")}>
            <Space direction="vertical" size={8} style={{ display: 'flex' }}>
              {sourceInstallation ? (
                <Tag
                  closable
                  color="blue"
                  onClose={() => setSourceInstallation(null)}
                >
                  {installationLabel(sourceInstallation)} · {sourceInstallation.nodeDisplayName}
                </Tag>
              ) : (
                <Typography.Text type="secondary">{tr("未指定来源（手动上传）")}</Typography.Text>
              )}
              <Button icon={<SelectOutlined />} onClick={() => setSourcePickerOpen(true)}>
                {tr("选择来源安装实例")}</Button>
            </Space>
          </Form.Item>
          <Form.Item name="payloadJson" label={tr("负载 JSON")} rules={[{ required: true }]}>
            <Input.TextArea rows={10} spellCheck={false} />
          </Form.Item>
          <Typography.Text type="secondary">
            {tr("不超过 64KB 直接内联；超过则转存文件（仍按内容哈希校验）。")}</Typography.Text>
        </Form>
      </Modal>

      <Modal
        open={pushTarget !== null}
        title={pushTarget ? tr("下发备份：{{arg0}}", { arg0: pushTarget.name }) : tr("下发备份")}
        confirmLoading={saving}
        onOk={() => void submitPush()}
        onCancel={closePushForm}
        width={responsiveWidth(isMobile, 640)}
      >
        <Form form={pushForm} layout="vertical">
          <Form.Item
            label={tr("目标安装实例")}
            required
            extra={tr("只允许启用且在线待机的安装实例；实例较多时可筛选，勾选跨页保留。")}
          >
            <Space direction="vertical" size={8} style={{ display: 'flex' }}>
              <Flex wrap gap={4}>
                {selectedList.length === 0 ? (
                  <Typography.Text type="secondary">{tr("尚未选择目标安装实例")}</Typography.Text>
                ) : (
                  selectedList.map((installation) => (
                    <Tag
                      key={installation.id}
                      closable
                      color={isInstallationSelectable(installation) ? 'blue' : 'default'}
                      onClose={() => removeSelectedInstallation(installation.id)}
                    >
                      {installationLabel(installation)} · {installation.nodeDisplayName}
                    </Tag>
                  ))
                )}
              </Flex>
              <Button icon={<SelectOutlined />} onClick={() => setPickerOpen(true)}>
                {tr("选择在线安装实例")}</Button>
              {sourceInstallationHint ? (
                <Typography.Text type="warning">{sourceInstallationHint}</Typography.Text>
              ) : null}
            </Space>
          </Form.Item>
          <Form.Item name="targetChannel" label={tr("目标渠道")}>
            <Input placeholder={pushTarget?.channel ?? 'default'} />
          </Form.Item>
          <Form.Item name="targetFrameworkVersion" label={tr("目标框架版本")}>
            <Input placeholder={pushTarget?.frameworkVersion ?? '1.0.0'} />
          </Form.Item>
          <Form.Item
            name="currentHash"
            label={tr("目标当前配置哈希（可选，提供时先落快照）")}
            extra={selectedList.length > 1 ? tr("多安装实例下发不支持快照：仅单选时可填。") : undefined}
          >
            <Input placeholder={tr("64 位十六进制")} disabled={selectedList.length > 1} />
          </Form.Item>
          <Form.Item name="force" label={tr("放宽主版本校验（渠道不一致仍拒绝）")} valuePropName="checked">
            <Switch />
          </Form.Item>
          {needsCrossInstallationConfirm ? (
            <Form.Item
              name="confirmCrossInstallation"
              label={tr("确认跨安装实例下发")}
              valuePropName="checked"
            >
              <Switch />
            </Form.Item>
          ) : null}
          <Typography.Text type="secondary">
            {tr("下发只是提示：目标客户端回拉下载并应用后逐台回执，超时视为失败。")}</Typography.Text>
        </Form>
      </Modal>

      <InstallationPickerModal
        open={sourcePickerOpen && backupOpen}
        appKey={appKey}
        selected={
          new Map<string, InstallationSummary>(
            sourceInstallation ? [[sourceInstallation.id, sourceInstallation]] : [],
          )
        }
        single
        mode="any"
        title={tr("选择来源安装实例")}
        onConfirm={(next) => {
          setSourceInstallation([...next.values()][0] ?? null);
          setSourcePickerOpen(false);
        }}
        onCancel={() => setSourcePickerOpen(false)}
      />

      <InstallationPickerModal
        open={pickerOpen && pushTarget !== null}
        appKey={appKey}
        selected={selectedInstallations}
        onConfirm={(next) => {
          setSelectedInstallations(next);
          setPickerOpen(false);
        }}
        onCancel={() => setPickerOpen(false)}
      />

      <Modal
        open={pushResult !== null}
        title={tr("下发结果")}
        onCancel={() => setPushResult(null)}
        width={responsiveWidth(isMobile, 680)}
        footer={
          <Button type="primary" onClick={() => setPushResult(null)}>
            {tr("知道了")}</Button>
        }
      >
        {pushResult ? (
          <Space direction="vertical" size={12} style={{ display: 'flex' }}>
            <Alert
              type={pushResult.result.success ? 'success' : 'warning'}
              showIcon
              message={
                pushResult.result.success
                  ? tr("全部目标已回执并应用")
                  : pushResult.result.message ?? tr("部分目标未成功")
              }
            />
            {pushResult.result.targets?.length ? (
              <Table<PushTargetResult>
                rowKey="installationId"
                size="small"
                pagination={false}
                dataSource={pushResult.result.targets}
                columns={targetResultColumns}
              />
            ) : null}
          </Space>
        ) : null}
      </Modal>
    </Space>
  );
}
