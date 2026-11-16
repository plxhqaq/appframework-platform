import { tr } from '../../i18n/translate';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Form, Input, Modal, Select, Switch } from 'antd';
import { useEffect } from 'react';
import { identityApi } from '../../shared/api/identity';
import type { CreateUserRequest, UpdateUserRequest, UserDetail } from '../../shared/api/types';

interface UserFormValues {
  userName: string;
  password?: string;
  displayName?: string;
  isEnabled: boolean;
  roleIds: string[];
  mustChangePassword: boolean;
}

interface UserFormModalProps {
  open: boolean;
  /** 为空表示新建。 */
  user: UserDetail | null;
  onClose: () => void;
}

const PASSWORD_PATTERN = /^(?=.*[A-Za-z])(?=.*\d).{8,128}$/;

/** 用户新建/编辑。用户名创建后不可改；口令只允许管理员通过"重置口令"单独操作。 */
export function UserFormModal({ open, user, onClose }: UserFormModalProps) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<UserFormValues>();
  const isEdit = user !== null;

  const roles = useQuery({
    queryKey: ['identity', 'roles'],
    queryFn: ({ signal }) => identityApi.listRoles(signal),
    enabled: open,
    staleTime: 60_000,
  });

  useEffect(() => {
    if (!open) {
      return;
    }

    form.setFieldsValue({
      userName: user?.userName ?? '',
      password: '',
      displayName: user?.displayName ?? '',
      isEnabled: user?.isEnabled ?? true,
      roleIds: user?.roleIds ?? [],
      mustChangePassword: true,
    });
  }, [open, user, form]);

  const mutation = useMutation({
    mutationFn: async (values: UserFormValues) => {
      if (user) {
        const body: UpdateUserRequest = {
          displayName: values.displayName ?? null,
          isEnabled: values.isEnabled,
          roleIds: values.roleIds,
        };
        return identityApi.updateUser(user.id, body);
      }

      const body: CreateUserRequest = {
        userName: values.userName,
        password: values.password ?? '',
        displayName: values.displayName ?? null,
        isEnabled: values.isEnabled,
        roleIds: values.roleIds,
        mustChangePassword: values.mustChangePassword,
      };
      return identityApi.createUser(body);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['identity', 'users'] });
      await queryClient.invalidateQueries({ queryKey: ['identity', 'roles'] });
      message.success(isEdit ? tr("用户已更新") : tr("用户已创建"));
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
      title={isEdit ? tr("编辑用户 · {{arg0}}", { arg0: user.userName }) : tr("新建用户")}
      okText={tr("保存")}
      cancelText={tr("取消")}
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      <Form<UserFormValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ isEnabled: true, roleIds: [], mustChangePassword: true }}
      >
        <Form.Item
          name="userName"
          label={tr("用户名")}
          tooltip={tr("小写字母或数字开头，3–64 位小写字母/数字/点/下划线/连字符；创建后不可修改。")}
          rules={[
            { required: true, message: tr("请输入用户名") },
            {
              pattern: /^[a-z0-9][a-z0-9._-]{2,63}$/,
              message: tr("格式：小写字母或数字开头，3–64 位小写字母/数字/点/下划线/连字符"),
            },
          ]}
        >
          <Input placeholder={tr("例如 alice")} disabled={isEdit} autoComplete="off" />
        </Form.Item>

        {!isEdit ? (
          <Form.Item
            name="password"
            label={tr("初始口令")}
            tooltip={tr("8–128 位，且同时包含字母与数字")}
            rules={[
              { required: true, message: tr("请输入初始口令") },
              { pattern: PASSWORD_PATTERN, message: tr("8–128 位，且同时包含字母与数字") },
            ]}
          >
            <Input.Password autoComplete="new-password" />
          </Form.Item>
        ) : null}

        <Form.Item name="displayName" label={tr("显示名")}>
          <Input placeholder={tr("可选")} autoComplete="off" />
        </Form.Item>

        <Form.Item name="roleIds" label={tr("角色")}>
          <Select
            mode="multiple"
            allowClear
            loading={roles.isPending}
            placeholder={tr("选择角色（可多选）")}
            optionFilterProp="label"
            options={(roles.data ?? []).map((role) => ({
              value: role.id,
              label: `${role.displayName ?? role.name}（${role.name}）`,
            }))}
          />
        </Form.Item>

        <Form.Item name="isEnabled" label={tr("启用")} valuePropName="checked">
          <Switch />
        </Form.Item>

        {!isEdit ? (
          <Form.Item
            name="mustChangePassword"
            label={tr("首次登录强制改密")}
            valuePropName="checked"
            tooltip={tr("管理员设定的初始口令对方已知，建议保持开启。")}
          >
            <Switch />
          </Form.Item>
        ) : null}
      </Form>
    </Modal>
  );
}
