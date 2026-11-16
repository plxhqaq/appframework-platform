import { formatDateTime } from '../../shared/format';
import { tr } from '../../i18n/translate';
import { KeyOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  DatePicker,
  Drawer,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import type { Dayjs } from 'dayjs';
import { useMemo, useState } from 'react';
import { appsApi } from '../../shared/api/apps';
import { identityApi } from '../../shared/api/identity';
import type { ApiKeySummary, AppDetail, CreatedApiKey, CreateApiKeyRequest } from '../../shared/api/types';
import { groupPermissionsByModule } from '../../shared/permissionCatalog';
import { describePermission } from '../../shared/permissionDescriptions';
import { PERMISSIONS } from '../../shared/permissions';
import { responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { useAuthStore } from '../../stores/auth';

interface ApiKeysDrawerProps {
  open: boolean;
  app: AppDetail | null;
  onClose: () => void;
}

interface IssueKeyFormValues {
  name?: string;
  scopes?: string[];
  expiresAt?: Dayjs | null;
}

interface ScopeTemplate {
  key: string;
  label: string;
  description: string;
  scopes: string[];
}

/** 常用签发模板：scope 只是勾选的起点，签发前仍可增删。 */
const SCOPE_TEMPLATES: ScopeTemplate[] = [
  {
    key: 'device-hub',
    get label() { return tr("设备连接"); },
    get description() { return tr("只保留应用上下文：可连 /hubs/devices 注册、心跳、接收下发；不能调用版本、文档、备份等接口。"); },
    scopes: [],
  },
  {
    key: 'device-field',
    get label() { return tr("现场设备（推荐）"); },
    get description() { return tr("设备连接 + 检查更新与下载、拉取文档、收发配置备份、在线/离线授权校验。"); },
    scopes: [
      PERMISSIONS.versionsReleaseRead,
      PERMISSIONS.versionsArtifactDownload,
      PERMISSIONS.documentRead,
      PERMISSIONS.backupRead,
      PERMISSIONS.backupCreate,
      PERMISSIONS.licenseRead,
    ],
  },
  {
    key: 'update',
    get label() { return tr("更新分发"); },
    get description() { return tr("检查更新、读取版本清单，下载安装包与更新器。"); },
    scopes: [PERMISSIONS.versionsReleaseRead, PERMISSIONS.versionsArtifactDownload],
  },
  {
    key: 'backup',
    get label() { return tr("配置备份"); },
    get description() { return tr("按设备列出、下载并上传配置备份。"); },
    scopes: [PERMISSIONS.backupRead, PERMISSIONS.backupCreate],
  },
  {
    key: 'docs-license',
    get label() { return tr("文档与授权"); },
    get description() { return tr("读取客户端文档清单并下载，查询在线授权状态、校验离线授权码。"); },
    scopes: [PERMISSIONS.documentRead, PERMISSIONS.licenseRead],
  },
];

function sameScopeSet(left: readonly string[], right: readonly string[]): boolean {
  return left.length === right.length && left.every((scope) => right.includes(scope));
}

/** API Key 管理：列表、签发（明文一次性展示）、吊销。 */
export function ApiKeysDrawer({ open, app, onClose }: ApiKeysDrawerProps) {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.appsApiKeyManage));
  const [issueOpen, setIssueOpen] = useState(false);
  const [created, setCreated] = useState<CreatedApiKey | null>(null);

  const keys = useQuery({
    queryKey: ['apps', app?.id, 'keys'],
    queryFn: ({ signal }) => appsApi.listKeys(app!.id, signal),
    enabled: open && app !== null,
  });

  // 合并目录包含该应用声明的权限，说明与分组名都取自清单。
  const catalog = useQuery({
    queryKey: ['platform', 'permissions', app?.key ?? 'platform'],
    queryFn: ({ signal }) => identityApi.permissions(signal, app?.key),
    enabled: open && app !== null,
    staleTime: 5 * 60_000,
  });

  const descriptions = useMemo(() => {
    const map = new Map<string, string | null | undefined>();
    for (const item of catalog.data ?? []) {
      map.set(item.code, item.description ?? describePermission(item.code));
    }
    return map;
  }, [catalog.data]);

  const revoke = useMutation({
    mutationFn: (keyId: string) => appsApi.revokeKey(app!.id, keyId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['apps', app?.id, 'keys'] });
      message.success(tr("密钥已吊销"));
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleRevoke = (key: ApiKeySummary) => {
    modal.confirm({
      title: tr("吊销密钥 {{arg0}}？", { arg0: key.prefix }),
      content: tr("已吊销的密钥立即失效，使用方换不到新令牌，且不可恢复。"),
      okText: tr("吊销"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => revoke.mutateAsync(key.id),
    });
  };

  const columns: TableColumnsType<ApiKeySummary> = [
    { title: tr("前缀"), dataIndex: 'prefix', key: 'prefix', width: 120, render: (prefix: string) => <Typography.Text code>{prefix}</Typography.Text> },
    { title: tr("用途"), dataIndex: 'name', key: 'name', width: 140 },
    {
      title: tr("权限范围"),
      dataIndex: 'scopes',
      key: 'scopes',
      render: (scopes: string[]) =>
        scopes.length === 0 ? (
          <Tooltip title={tr("只保留应用上下文：可连设备 Hub，不能调用版本、文档、备份等接口")}>
            <Typography.Text type="secondary">{tr("仅应用上下文")}</Typography.Text>
          </Tooltip>
        ) : (
          scopes.map((scope) => (
            <Tooltip key={scope} title={descriptions.get(scope) ?? scope}>
              <Tag>{scope}</Tag>
            </Tooltip>
          ))
        ),
    },
    {
      title: tr("状态"),
      key: 'status',
      width: 90,
      render: (_: unknown, key: ApiKeySummary) =>
        key.revokedAt ? <Tag>{tr("已吊销")}</Tag> : key.isActive ? <Tag color="green">{tr("有效")}</Tag> : <Tag color="orange">{tr("已过期")}</Tag>,
    },
    {
      title: tr("过期"),
      dataIndex: 'expiresAt',
      key: 'expiresAt',
      width: 170,
      responsive: ['md'],
      render: (value: string | null) => (value ? formatDateTime(value) : tr("永不过期")),
    },
    {
      title: tr("最近使用"),
      dataIndex: 'lastUsedAt',
      key: 'lastUsedAt',
      width: 170,
      responsive: ['md'],
      render: (value: string | null) => (value ? formatDateTime(value) : '-'),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 90,
      fixed: 'right',
      render: (_: unknown, key: ApiKeySummary) =>
        canManage && key.isActive ? (
          <Button type="link" size="small" danger onClick={() => handleRevoke(key)}>
            {tr("吊销")}</Button>
        ) : null,
    },
  ];

  return (
    <Drawer
      open={open}
      width={responsiveWidth(isMobile, 860)}
      title={app ? tr("API 密钥 · {{arg0}}", { arg0: app.key }) : tr("API 密钥")}
      onClose={onClose}
      styles={{ body: { padding: isMobile ? 12 : 24 } }}
      destroyOnClose
      extra={
        canManage ? (
          <Button type="primary" icon={<KeyOutlined />} onClick={() => setIssueOpen(true)}>
            {tr("签发密钥")}</Button>
        ) : null
      }
    >
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 16 }}
        message={tr("M2M 换令牌方式")}
        description={
          <Typography.Text>
            {tr("请求头")}<Typography.Text code>X-App-Key: {app?.key ?? '<app>'}</Typography.Text> + <Typography.Text code>{tr("X-Api-Key: &lt;明文&gt;")}</Typography.Text>，
            POST <Typography.Text code>/api/v1/apps/token</Typography.Text> {tr("换取访问令牌。密钥明文只在签发时显示一次。")}</Typography.Text>
        }
      />

      <Table<ApiKeySummary>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={keys.isPending}
        dataSource={keys.data ?? []}
        columns={columns}
        pagination={false}
        locale={{ emptyText: tr("尚未签发密钥") }}
      />

      <IssueKeyModal
        open={issueOpen}
        app={app}
        onClose={() => setIssueOpen(false)}
        onIssued={(issued) => {
          setIssueOpen(false);
          setCreated(issued);
          void queryClient.invalidateQueries({ queryKey: ['apps', app?.id, 'keys'] });
        }}
      />

      <Modal
        open={created !== null}
        title={tr("密钥已签发")}
        okText={tr("我已保存")}
        cancelButtonProps={{ style: { display: 'none' } }}
        onOk={() => setCreated(null)}
        onCancel={() => setCreated(null)}
        maskClosable={false}
      >
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Alert type="warning" showIcon message={tr("明文只显示这一次，关闭后无法再次查看。")} />
          <Typography.Paragraph copyable={{ text: created?.plaintext ?? '' }} style={{ marginBottom: 0 }}>
            <Typography.Text code style={{ wordBreak: 'break-all' }}>
              {created?.plaintext}
            </Typography.Text>
          </Typography.Paragraph>
        </Space>
      </Modal>
    </Drawer>
  );
}

interface IssueKeyModalProps {
  open: boolean;
  app: AppDetail | null;
  onClose: () => void;
  onIssued: (issued: CreatedApiKey) => void;
}

function IssueKeyModal({ open, app, onClose, onIssued }: IssueKeyModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<IssueKeyFormValues>();
  const grantedPermissions = useAuthStore((state) => state.session?.permissions);

  const permissions = useQuery({
    queryKey: ['platform', 'permissions', app?.key ?? 'platform'],
    queryFn: ({ signal }) => identityApi.permissions(signal, app?.key),
    enabled: open && app !== null,
    staleTime: 5 * 60_000,
  });

  const manifest = useQuery({
    queryKey: ['apps', app?.key, 'permissions'],
    queryFn: ({ signal }) => appsApi.getPermissions(app!.key, signal),
    enabled: open && app !== null,
    staleTime: 60_000,
  });

  const descriptionOf = useMemo(() => {
    const map = new Map<string, string | null | undefined>();
    for (const item of permissions.data ?? []) {
      map.set(item.code, item.description ?? describePermission(item.code));
    }
    return map;
  }, [permissions.data]);

  const templates = useMemo<ScopeTemplate[]>(
    () => [
      ...SCOPE_TEMPLATES,
      ...(manifest.data?.presets ?? []).map((preset) => ({
        key: `app-preset-${preset.key}`,
        label: preset.label,
        description: preset.description ?? preset.label,
        scopes: preset.permissions,
      })),
    ],
    [manifest.data],
  );

  // 服务端只允许授予自己已持有的权限，这里提前过滤掉会被 606 拒绝的选项。
  const scopeOptions = useMemo(() => {
    const granted = new Set(grantedPermissions ?? []);
    const canGrantAll = granted.has('*');
    return groupPermissionsByModule(permissions.data ?? [])
      .map((group) => ({
        label: tr('{{label}}（{{module}}）', { label: group.label, module: group.moduleKey }),
        options: group.items
          .filter((item) => canGrantAll || granted.has(item.code))
          .map((item) => ({
            value: item.code,
            label: item.platformAdminOnly
              ? tr('{{code}}（管理员）', { code: item.code })
              : item.code,
          })),
      }))
      .filter((group) => group.options.length > 0);
  }, [permissions.data, grantedPermissions]);

  const availableScopes = useMemo(
    () => new Set(scopeOptions.flatMap((group) => group.options.map((option) => option.value))),
    [scopeOptions],
  );

  const adminOnlyScopes = useMemo(
    () =>
      new Set(
        (permissions.data ?? [])
          .filter((permission) => permission.platformAdminOnly)
          .map((permission) => permission.code),
      ),
    [permissions.data],
  );

  const selectedScopes = (Form.useWatch('scopes', form) as string[] | undefined) ?? [];

  const mutation = useMutation({
    mutationFn: (values: IssueKeyFormValues) => {
      const body: CreateApiKeyRequest = {
        name: values.name ?? null,
        scopes: values.scopes ?? [],
        expiresAt: values.expiresAt ? values.expiresAt.toISOString() : null,
      };
      return appsApi.createKey(app!.id, body);
    },
    onSuccess: (issued) => {
      message.success(tr("密钥已签发"));
      form.resetFields();
      onIssued(issued);
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleOk = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    try {
      await mutation.mutateAsync(values);
    } catch {
      // 错误已由 onError 提示
    }
  };

  return (
    <Modal
      open={open}
      title={tr("签发 API 密钥 · {{arg0}}", { arg0: app?.key ?? '' })}
      okText={tr("签发")}
      cancelText={tr("取消")}
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      <Form<IssueKeyFormValues> form={form} layout="vertical" requiredMark={false}>
        <Form.Item name="name" label={tr("用途")}>
          <Input placeholder={tr("例如 CI 发布流水线")} autoComplete="off" />
        </Form.Item>
        {permissions.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={tr("权限目录加载失败，暂时无法选择权限范围（仍可签发仅设备连接的密钥）。")}
            action={
              <Button size="small" onClick={() => void permissions.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Form.Item
          label={tr("权限范围")}
          tooltip={tr("只能选择当前账号已持有的权限，不得使用通配符 *。留空只保留应用上下文（仅能连设备 Hub）。")}
        >
          <Space direction="vertical" size={8} style={{ width: '100%' }}>
            <div>
              <Typography.Text type="secondary">{tr("常用模板（点击回填，可再调整）：")}</Typography.Text>
              <div style={{ marginTop: 4 }}>
                <Space size={4} wrap>
                  {templates.map((template) => {
                    const missing = permissions.isSuccess
                      ? template.scopes.filter((scope) => !availableScopes.has(scope))
                      : [];
                    const disabled = missing.length > 0;
                    return (
                      <Tooltip
                        key={template.key}
                        title={
                          disabled
                            ? tr("当前账号未持有：{{arg0}}", { arg0: missing.join('、') })
                            : template.description
                        }
                      >
                        <Tag.CheckableTag
                          checked={sameScopeSet(selectedScopes, template.scopes)}
                          style={disabled ? { opacity: 0.45, cursor: 'not-allowed' } : undefined}
                          onChange={() => {
                            if (disabled) {
                              return;
                            }
                            form.setFieldValue('scopes', [...template.scopes]);
                          }}
                        >
                          {template.label}
                        </Tag.CheckableTag>
                      </Tooltip>
                    );
                  })}
                </Space>
              </div>
            </div>
            <Form.Item name="scopes" noStyle>
              <Select
                mode="multiple"
                allowClear
                showSearch
                optionFilterProp="label"
                maxTagCount="responsive"
                loading={permissions.isPending}
                placeholder={tr("选择权限码；留空只能连设备 Hub")}
                options={scopeOptions}
                notFoundContent={permissions.isPending ? tr("加载权限目录…") : tr("无匹配权限码")}
                optionRender={(option) => {
                  const code = String(option.value);
                  return (
                    <div style={{ padding: '2px 0', lineHeight: 1.4 }}>
                      <Space size={6}>
                        <Typography.Text code>{code}</Typography.Text>
                        {adminOnlyScopes.has(code) ? <Tag color="gold">{tr("管理员")}</Tag> : null}
                      </Space>
                      <div>
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                          {descriptionOf.get(code) ?? tr("暂无说明")}
                        </Typography.Text>
                      </div>
                    </div>
                  );
                }}
              />
            </Form.Item>
          </Space>
        </Form.Item>
        <Form.Item name="expiresAt" label={tr("过期时间")}>
          <DatePicker showTime style={{ width: '100%' }} placeholder={tr("留空表示永不过期")} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
