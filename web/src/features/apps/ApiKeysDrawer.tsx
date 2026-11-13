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
  Typography,
} from 'antd';
import type { Dayjs } from 'dayjs';
import { useState } from 'react';
import { appsApi } from '../../shared/api/apps';
import type { ApiKeySummary, AppDetail, CreatedApiKey, CreateApiKeyRequest } from '../../shared/api/types';
import { PERMISSIONS } from '../../shared/permissions';
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

/** API Key 管理：列表、签发（明文一次性展示）、吊销。 */
export function ApiKeysDrawer({ open, app, onClose }: ApiKeysDrawerProps) {
  const { message, modal } = App.useApp();
  const queryClient = useQueryClient();
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.appsApiKeyManage));
  const [issueOpen, setIssueOpen] = useState(false);
  const [created, setCreated] = useState<CreatedApiKey | null>(null);

  const keys = useQuery({
    queryKey: ['apps', app?.id, 'keys'],
    queryFn: ({ signal }) => appsApi.listKeys(app!.id, signal),
    enabled: open && app !== null,
  });

  const revoke = useMutation({
    mutationFn: (keyId: string) => appsApi.revokeKey(app!.id, keyId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['apps', app?.id, 'keys'] });
      message.success('密钥已吊销');
    },
    onError: (error: Error) => message.error(error.message),
  });

  const handleRevoke = (key: ApiKeySummary) => {
    modal.confirm({
      title: `吊销密钥 ${key.prefix}？`,
      content: '已吊销的密钥立即失效，使用方换不到新令牌，且不可恢复。',
      okText: '吊销',
      okButtonProps: { danger: true },
      cancelText: '取消',
      onOk: () => revoke.mutateAsync(key.id),
    });
  };

  const columns = [
    { title: '前缀', dataIndex: 'prefix', key: 'prefix', width: 120, render: (prefix: string) => <Typography.Text code>{prefix}</Typography.Text> },
    { title: '用途', dataIndex: 'name', key: 'name', width: 140 },
    {
      title: '权限范围',
      dataIndex: 'scopes',
      key: 'scopes',
      render: (scopes: string[]) =>
        scopes.length === 0 ? <Typography.Text type="secondary">仅只读</Typography.Text> : scopes.map((scope) => <Tag key={scope}>{scope}</Tag>),
    },
    {
      title: '状态',
      key: 'status',
      width: 90,
      render: (_: unknown, key: ApiKeySummary) =>
        key.revokedAt ? <Tag>已吊销</Tag> : key.isActive ? <Tag color="green">有效</Tag> : <Tag color="orange">已过期</Tag>,
    },
    {
      title: '过期',
      dataIndex: 'expiresAt',
      key: 'expiresAt',
      width: 170,
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '永不过期'),
    },
    {
      title: '最近使用',
      dataIndex: 'lastUsedAt',
      key: 'lastUsedAt',
      width: 170,
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '-'),
    },
    {
      title: '操作',
      key: 'actions',
      width: 90,
      render: (_: unknown, key: ApiKeySummary) =>
        canManage && key.isActive ? (
          <Button type="link" size="small" danger onClick={() => handleRevoke(key)}>
            吊销
          </Button>
        ) : null,
    },
  ];

  return (
    <Drawer
      open={open}
      width={860}
      title={app ? `API 密钥 · ${app.key}` : 'API 密钥'}
      onClose={onClose}
      destroyOnClose
      extra={
        canManage ? (
          <Button type="primary" onClick={() => setIssueOpen(true)}>
            签发密钥
          </Button>
        ) : null
      }
    >
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 16 }}
        message="M2M 换令牌方式"
        description={
          <Typography.Text>
            请求头 <Typography.Text code>X-App-Key: {app?.key ?? '<app>'}</Typography.Text> + <Typography.Text code>X-Api-Key: &lt;明文&gt;</Typography.Text>，
            POST <Typography.Text code>/api/v1/apps/token</Typography.Text> 换取访问令牌。密钥明文只在签发时显示一次。
          </Typography.Text>
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
        locale={{ emptyText: '尚未签发密钥' }}
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
        title="密钥已签发"
        okText="我已保存"
        cancelButtonProps={{ style: { display: 'none' } }}
        onOk={() => setCreated(null)}
        onCancel={() => setCreated(null)}
        maskClosable={false}
      >
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Alert type="warning" showIcon message="明文只显示这一次，关闭后无法再次查看。" />
          <Typography.Paragraph copyable={{ text: created?.plaintext ?? '' }} style={{ marginBottom: 0 }}>
            <Typography.Text code>{created?.plaintext}</Typography.Text>
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
      message.success('密钥已签发');
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
      title={`签发 API 密钥 · ${app?.key ?? ''}`}
      okText="签发"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      <Form<IssueKeyFormValues> form={form} layout="vertical" requiredMark={false}>
        <Form.Item name="name" label="用途">
          <Input placeholder="例如 CI 发布流水线" autoComplete="off" />
        </Form.Item>
        <Form.Item
          name="scopes"
          label="权限范围"
          tooltip="必须是当前管理员权限的子集；不得使用通配符 *。留空表示仅本应用只读。"
        >
          <Select mode="tags" placeholder="输入权限码后回车，例如 versions.release.read" tokenSeparators={[',', ' ']} />
        </Form.Item>
        <Form.Item name="expiresAt" label="过期时间">
          <DatePicker showTime style={{ width: '100%' }} placeholder="留空表示永不过期" />
        </Form.Item>
      </Form>
    </Modal>
  );
}
