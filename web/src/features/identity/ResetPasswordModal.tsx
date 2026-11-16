import { tr } from '../../i18n/translate';
import { useMutation } from '@tanstack/react-query';
import { App, Form, Input, Modal, Switch, Typography } from 'antd';
import { useEffect } from 'react';
import { identityApi } from '../../shared/api/identity';
import type { UserSummary } from '../../shared/api/types';

interface ResetPasswordValues {
  newPassword: string;
  requireChange: boolean;
}

interface ResetPasswordModalProps {
  open: boolean;
  user: UserSummary | null;
  onClose: () => void;
}

const PASSWORD_PATTERN = /^(?=.*[A-Za-z])(?=.*\d).{8,128}$/;

/** 管理员重置口令：成功后服务端吊销该用户全部会话，所以这里不刷新当前登录态。 */
export function ResetPasswordModal({ open, user, onClose }: ResetPasswordModalProps) {
  const { message } = App.useApp();
  const [form] = Form.useForm<ResetPasswordValues>();

  useEffect(() => {
    if (!open) {
      return;
    }
    form.setFieldsValue({ newPassword: '', requireChange: true });
  }, [open, form]);

  const mutation = useMutation({
    mutationFn: (values: ResetPasswordValues) =>
      identityApi.resetPassword(user!.id, {
        newPassword: values.newPassword,
        requireChange: values.requireChange,
      }),
    onSuccess: (result) => {
      message.success(tr("口令已重置，已吊销 {{arg0}} 个会话", { arg0: result.revokedSessions }));
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
      title={user ? tr("重置口令 · {{arg0}}", { arg0: user.userName }) : tr("重置口令")}
      okText={tr("重置")}
      cancelText={tr("取消")}
      confirmLoading={mutation.isPending}
      onCancel={onClose}
      onOk={() => void handleOk()}
      afterClose={() => {
        mutation.reset();
        form.resetFields();
      }}
    >
      <Typography.Paragraph type="secondary">
        {tr("新口令只显示在管理员输入框里；提交后该用户全部已登录设备都会被强制下线。")}</Typography.Paragraph>

      <Form<ResetPasswordValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        initialValues={{ newPassword: '', requireChange: true }}
      >
        <Form.Item
          name="newPassword"
          label={tr("新口令")}
          tooltip={tr("8–128 位，且同时包含字母与数字")}
          rules={[
            { required: true, message: tr("请输入新口令") },
            { pattern: PASSWORD_PATTERN, message: tr("8–128 位，且同时包含字母与数字") },
          ]}
        >
          <Input.Password autoComplete="new-password" />
        </Form.Item>

        <Form.Item
          name="requireChange"
          label={tr("下次登录强制改密")}
          valuePropName="checked"
          tooltip={tr("建议保持开启：管理员设定临时口令后，用户应立即换成自己的口令。")}
        >
          <Switch />
        </Form.Item>
      </Form>
    </Modal>
  );
}
