import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Form, Input, Modal, Select, Space, Tag, Tree, Typography } from 'antd';
import type { DataNode } from 'antd/es/tree';
import { useEffect, useMemo, useState } from 'react';
import { appsApi } from '../../shared/api/apps';
import { identityApi } from '../../shared/api/identity';
import type {
  CreateRoleRequest,
  PermissionDescriptor,
  RoleSummary,
  UpdateRoleRequest,
} from '../../shared/api/types';

interface RoleFormValues {
  name: string;
  displayName?: string;
  scope: 'platform' | 'app';
  appKey?: string;
}

interface RoleFormModalProps {
  open: boolean;
  /** 为空表示新建。 */
  role: RoleSummary | null;
  onClose: () => void;
}

const MODULE_LABELS: Record<string, string> = {
  apps: '应用接入',
  identity: '身份与权限',
  versions: '版本与渠道',
  licensing: '授权',
  backups: '备份',
  documents: '文档',
  webhooks: 'Webhook 订阅',
  platform: '平台',
  client: '客户端',
};

const GROUP_PREFIX = 'group:';

/** 权限树按模块分组；分组节点只为展示，提交时只取叶子权限码。 */
function buildTreeData(permissions: PermissionDescriptor[]): DataNode[] {
  const groups = new Map<string, PermissionDescriptor[]>();
  permissions.forEach((permission) => {
    const list = groups.get(permission.moduleKey) ?? [];
    list.push(permission);
    groups.set(permission.moduleKey, list);
  });

  return [...groups.entries()]
    .sort(([left], [right]) => {
      const leftSort = Math.min(...groups.get(left)!.map((item) => item.sort));
      const rightSort = Math.min(...groups.get(right)!.map((item) => item.sort));
      return leftSort - rightSort;
    })
    .map(([moduleKey, items]) => ({
      key: `${GROUP_PREFIX}${moduleKey}`,
      title: `${MODULE_LABELS[moduleKey] ?? moduleKey}（${moduleKey}）`,
      selectable: false,
      children: [...items]
        .sort((left, right) => left.sort - right.sort)
        .map((item) => ({
          key: item.code,
          title: (
            <Space size={4}>
              <Typography.Text code>{item.code}</Typography.Text>
              {item.platformAdminOnly ? <Tag color="gold">管理员</Tag> : null}
            </Space>
          ),
        })),
    }));
}

/** 角色新建/编辑：权限树来自 `/api/v1/platform/permissions`，是服务端唯一事实源。 */
export function RoleFormModal({ open, role, onClose }: RoleFormModalProps) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<RoleFormValues>();
  const isEdit = role !== null;
  const isPlatformAdmin = role?.name === 'platform-admin';
  const scope = Form.useWatch('scope', form) ?? role?.scope ?? 'platform';

  const [checkedPermissions, setCheckedPermissions] = useState<string[]>([]);

  const permissions = useQuery({
    queryKey: ['platform', 'permissions'],
    queryFn: ({ signal }) => identityApi.permissions(signal),
    enabled: open,
    staleTime: 5 * 60_000,
  });

  const apps = useQuery({
    queryKey: ['apps', 'list'],
    queryFn: ({ signal }) => appsApi.list(signal),
    enabled: open && scope === 'app',
    staleTime: 60_000,
  });

  const treeData = useMemo(() => buildTreeData(permissions.data ?? []), [permissions.data]);

  useEffect(() => {
    if (!open) {
      return;
    }

    const permissionsValue = role?.permissions ?? [];
    form.setFieldsValue({
      name: role?.name ?? '',
      displayName: role?.displayName ?? '',
      scope: role?.scope === 'app' ? 'app' : 'platform',
      appKey: role?.appKey ?? undefined,
    });
    setCheckedPermissions(permissionsValue);
  }, [open, role, form]);

  const mutation = useMutation({
    mutationFn: async (values: RoleFormValues) => {
      const effectivePermissions = isPlatformAdmin ? ['*'] : checkedPermissions;

      if (role) {
        const body: UpdateRoleRequest = {
          displayName: values.displayName ?? null,
          scope: values.scope,
          appKey: values.scope === 'app' ? (values.appKey ?? null) : null,
          permissions: effectivePermissions,
        };
        return identityApi.updateRole(role.id, body);
      }

      const body: CreateRoleRequest = {
        name: values.name,
        displayName: values.displayName ?? null,
        scope: values.scope,
        appKey: values.scope === 'app' ? (values.appKey ?? null) : null,
        permissions: effectivePermissions,
      };
      return identityApi.createRole(body);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['identity', 'roles'] });
      await queryClient.invalidateQueries({ queryKey: ['identity', 'users'] });
      message.success(isEdit ? '角色已更新' : '角色已创建');
      onClose();
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
      width={720}
      title={isEdit ? `编辑角色 · ${role.name}` : '新建角色'}
      okText="保存"
      cancelText="取消"
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
        setCheckedPermissions([]);
      }}
    >
      {isPlatformAdmin ? (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 16 }}
          message="内置平台管理员角色固定持有通配符权限（*），不可收窄，也不可删除。"
        />
      ) : null}

      <Form<RoleFormValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ scope: 'platform' }}
      >
        <Space size="middle" style={{ display: 'flex', alignItems: 'flex-start' }}>
          <Form.Item
            name="name"
            label="角色名"
            tooltip="小写字母开头，2–64 位小写字母/数字/连字符；创建后不可修改。"
            style={{ flex: 1 }}
            rules={[
              { required: true, message: '请输入角色名' },
              {
                pattern: /^[a-z][a-z0-9-]{1,63}$/,
                message: '格式：小写字母开头，2–64 位小写字母/数字/连字符',
              },
            ]}
          >
            <Input placeholder="例如 ops-reader" disabled={isEdit} autoComplete="off" />
          </Form.Item>

          <Form.Item name="displayName" label="显示名" style={{ flex: 1 }}>
            <Input placeholder="可选" autoComplete="off" />
          </Form.Item>
        </Space>

        <Space size="middle" style={{ display: 'flex', alignItems: 'flex-start' }}>
          <Form.Item
            name="scope"
            label="作用域"
            style={{ width: 180 }}
            tooltip="platform 角色用于管理端；app 角色只在该应用上下文生效。"
            rules={[{ required: true }]}
          >
            <Select
              disabled={isPlatformAdmin}
              options={[
                { value: 'platform', label: '平台级（platform）' },
                { value: 'app', label: '应用级（app）' },
              ]}
              onChange={(value: 'platform' | 'app') => {
                if (value === 'platform') {
                  form.setFieldValue('appKey', undefined);
                }
              }}
            />
          </Form.Item>

          {scope === 'app' ? (
            <Form.Item
              name="appKey"
              label="应用键"
              style={{ flex: 1 }}
              rules={[{ required: true, message: '应用级角色必须选择应用' }]}
            >
              <Select
                showSearch
                optionFilterProp="label"
                loading={apps.isPending}
                placeholder="选择应用"
                options={(apps.data ?? []).map((app) => ({
                  value: app.key,
                  label: `${app.name}（${app.key}）`,
                }))}
              />
            </Form.Item>
          ) : null}
        </Space>

        <Form.Item label="权限" required>
          {permissions.isPending ? <Typography.Text type="secondary">加载权限目录…</Typography.Text> : null}
          {permissions.isError ? (
            <Alert type="error" showIcon message={(permissions.error as Error).message} />
          ) : null}
          {treeData.length > 0 ? (
            <div style={{ maxHeight: 320, overflow: 'auto', border: '1px solid #f0f0f0', borderRadius: 6, padding: 8 }}>
              <Tree
                key={role?.id ?? 'new'}
                checkable
                defaultExpandAll
                disabled={isPlatformAdmin}
                treeData={treeData}
                checkedKeys={isPlatformAdmin ? ['*'] : checkedPermissions}
                onCheck={(keys) => {
                  const list = Array.isArray(keys) ? keys : keys.checked;
                  setCheckedPermissions(
                    list.map(String).filter((key) => !key.startsWith(GROUP_PREFIX)),
                  );
                }}
              />
            </div>
          ) : null}
        </Form.Item>
      </Form>
    </Modal>
  );
}
