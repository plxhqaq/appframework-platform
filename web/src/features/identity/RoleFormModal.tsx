import { tr } from '../../i18n/translate';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Flex, Form, Input, Modal, Select, Space, Tag, Tooltip, Tree, Typography } from 'antd';
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
import { groupPermissionsByModule } from '../../shared/permissionCatalog';
import { describePermission } from '../../shared/permissionDescriptions';
import { responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';

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

const GROUP_PREFIX = 'group:';

/** 权限树按模块分组；分组节点只为展示，提交时只取叶子权限码。 */
function buildTreeData(permissions: PermissionDescriptor[]): DataNode[] {
  return groupPermissionsByModule(permissions).map((group) => ({
    key: `${GROUP_PREFIX}${group.moduleKey}`,
    title: group.moduleKey.startsWith('app:') ? group.label : `${group.label}（${group.moduleKey}）`,
    selectable: false,
    children: group.items.map((item) => ({
      key: item.code,
      title: (
        <Space size={[6, 2]} wrap>
          <Typography.Text code>{item.code}</Typography.Text>
          {item.platformAdminOnly ? <Tag color="gold">{tr("管理员")}</Tag> : null}
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {item.description ?? describePermission(item.code) ?? tr("暂无说明")}
          </Typography.Text>
        </Space>
      ),
    })),
  }));
}

/** 角色新建/编辑：权限树来自 `/api/v1/platform/permissions`，是服务端唯一事实源。 */
export function RoleFormModal({ open, role, onClose }: RoleFormModalProps) {
  const { message } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<RoleFormValues>();
  const isEdit = role !== null;
  const isPlatformAdmin = role?.name === 'platform-admin';
  const scope = Form.useWatch('scope', form) ?? role?.scope ?? 'platform';
  const appKey = Form.useWatch('appKey', form) ?? role?.appKey ?? undefined;
  const activeAppKey = scope === 'app' ? appKey : undefined;

  const [checkedPermissions, setCheckedPermissions] = useState<string[]>([]);

  const permissions = useQuery({
    queryKey: ['platform', 'permissions', activeAppKey ?? 'platform'],
    queryFn: ({ signal }) => identityApi.permissions(signal, activeAppKey),
    enabled: open && (scope === 'platform' || Boolean(activeAppKey)),
    staleTime: 5 * 60_000,
  });

  const manifest = useQuery({
    queryKey: ['apps', activeAppKey, 'permissions'],
    queryFn: ({ signal }) => appsApi.getPermissions(activeAppKey!, signal),
    enabled: open && Boolean(activeAppKey),
    staleTime: 60_000,
  });

  const presets = manifest.data?.presets ?? [];

  useEffect(() => {
    // 切换应用后剔除不在当前目录里的历史权限码，避免提交时被服务端判为未知权限。
    if (!permissions.isSuccess) {
      return;
    }

    const available = new Set((permissions.data ?? []).map((item) => item.code));
    setCheckedPermissions((previous) => previous.filter((code) => available.has(code)));
  }, [permissions.data, permissions.isSuccess]);

  const togglePreset = (codes: string[]) => {
    setCheckedPermissions((previous) => {
      const allChecked = codes.every((code) => previous.includes(code));
      return allChecked
        ? previous.filter((code) => !codes.includes(code))
        : [...new Set([...previous, ...codes])];
    });
  };

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
      message.success(isEdit ? tr("角色已更新") : tr("角色已创建"));
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
      width={responsiveWidth(isMobile, 720)}
      title={isEdit ? tr("编辑角色 · {{arg0}}", { arg0: role.name }) : tr("新建角色")}
      okText={tr("保存")}
      cancelText={tr("取消")}
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
          message={tr("内置平台管理员角色固定持有通配符权限（*），不可收窄，也不可删除。")}
        />
      ) : null}

      <Form<RoleFormValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ scope: 'platform' }}
      >
        <Flex wrap gap={12} align="flex-start">
          <Form.Item
            name="name"
            label={tr("角色名")}
            tooltip={tr("小写字母开头，2–64 位小写字母/数字/连字符；创建后不可修改。")}
            style={{ flex: '1 1 260px', minWidth: 0 }}
            rules={[
              { required: true, message: tr("请输入角色名") },
              {
                pattern: /^[a-z][a-z0-9-]{1,63}$/,
                message: tr("格式：小写字母开头，2–64 位小写字母/数字/连字符"),
              },
            ]}
          >
            <Input placeholder={tr("例如 ops-reader")} disabled={isEdit} autoComplete="off" />
          </Form.Item>

          <Form.Item name="displayName" label={tr("显示名")} style={{ flex: '1 1 260px', minWidth: 0 }}>
            <Input placeholder={tr("可选")} autoComplete="off" />
          </Form.Item>
        </Flex>

        <Flex wrap gap={12} align="flex-start">
          <Form.Item
            name="scope"
            label={tr("作用域")}
            style={{ width: 180, maxWidth: '100%' }}
            tooltip={tr("platform 角色用于管理端；app 角色只在该应用上下文生效。")}
            rules={[{ required: true }]}
          >
            <Select
              disabled={isPlatformAdmin}
              options={[
                { value: 'platform', label: tr("平台级（platform）") },
                { value: 'app', label: tr("应用级（app）") },
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
              label={tr("应用键")}
              style={{ flex: '1 1 260px', minWidth: 0 }}
              rules={[{ required: true, message: tr("应用级角色必须选择应用") }]}
            >
              <Select
                showSearch
                optionFilterProp="label"
                loading={apps.isPending}
                placeholder={tr("选择应用")}
                options={(apps.data ?? []).map((app) => ({
                  value: app.key,
                  label: `${app.name}（${app.key}）`,
                }))}
              />
            </Form.Item>
          ) : null}
        </Flex>

        <Form.Item label={tr("权限")} required>
          {permissions.isPending ? <Typography.Text type="secondary">{tr("加载权限目录…")}</Typography.Text> : null}
          {permissions.isError ? (
            <Alert
              type="error"
              showIcon
              message={(permissions.error as Error).message}
              action={
                <Button size="small" onClick={() => void permissions.refetch()}>
                  {tr("重试")}</Button>
              }
            />
          ) : null}
          {activeAppKey && manifest.data?.locked ? (
            <Alert
              type="info"
              showIcon
              style={{ marginBottom: 8 }}
              message={tr("该应用的权限清单已锁定；应用需要重新上传时，请先在应用详情页人工解锁。")}
            />
          ) : null}
          {presets.length > 0 ? (
            <div style={{ marginBottom: 8 }}>
              <Typography.Text type="secondary">{tr("常用组合（点击追加或移除）：")}</Typography.Text>
              <div style={{ marginTop: 4 }}>
                <Space size={4} wrap>
                  {presets.map((preset) => (
                    <Tooltip key={preset.key} title={preset.description ?? preset.label}>
                      <Tag.CheckableTag
                        checked={preset.permissions.every((code) => checkedPermissions.includes(code))}
                        onChange={() => togglePreset(preset.permissions)}
                      >
                        {preset.label}
                      </Tag.CheckableTag>
                    </Tooltip>
                  ))}
                </Space>
              </div>
            </div>
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
