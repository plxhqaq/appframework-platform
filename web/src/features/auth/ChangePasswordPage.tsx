import { App, Button, Card, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { authApi } from '../../shared/api/auth';
import { useAuthStore } from '../../stores/auth';

interface ChangePasswordValues {
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
}

const PASSWORD_PATTERN = /^(?=.*[A-Za-z])(?=.*\d).{8,128}$/;

/**
 * 自助改密。成功后服务端会吊销全部刷新令牌，所以这里直接清空登录态重新登录，
 * 避免"页面还开着、会话其实已经失效"的错觉。
 */
export function ChangePasswordPage() {
  const navigate = useNavigate();
  const { message } = App.useApp();
  const session = useAuthStore((state) => state.session);
  const signOut = useAuthStore((state) => state.signOut);
  const [form] = Form.useForm<ChangePasswordValues>();
  const [pending, setPending] = useState(false);

  const handleFinish = async (values: ChangePasswordValues) => {
    setPending(true);
    try {
      const result = await authApi.changePassword(values.currentPassword, values.newPassword);
      message.success(`口令已修改，已吊销 ${result.revokedSessions} 个会话，请重新登录`);
      await signOut();
      navigate('/login', { replace: true });
    } catch (caught) {
      message.error(caught instanceof Error ? caught.message : '修改口令失败');
    } finally {
      setPending(false);
    }
  };

  return (
    <Card title="修改口令" size="small" style={{ maxWidth: 520 }}>
      <Typography.Paragraph type="secondary">
        {session?.mustChangePassword
          ? '当前账号要求先修改初始口令。修改后全部已登录设备都需要重新登录。'
          : '修改后全部已登录设备都需要重新登录。'}
      </Typography.Paragraph>

      <Form<ChangePasswordValues>
        form={form}
        layout="vertical"
        requiredMark={false}
        onFinish={handleFinish}
      >
        <Form.Item
          name="currentPassword"
          label="当前口令"
          rules={[{ required: true, message: '请输入当前口令' }]}
        >
          <Input.Password autoComplete="current-password" />
        </Form.Item>

        <Form.Item
          name="newPassword"
          label="新口令"
          tooltip="8–128 位，且同时包含字母与数字"
          rules={[
            { required: true, message: '请输入新口令' },
            { pattern: PASSWORD_PATTERN, message: '8–128 位，且同时包含字母与数字' },
          ]}
        >
          <Input.Password autoComplete="new-password" />
        </Form.Item>

        <Form.Item
          name="confirmPassword"
          label="确认新口令"
          dependencies={['newPassword']}
          rules={[
            { required: true, message: '请再次输入新口令' },
            ({ getFieldValue }) => ({
              validator(_, value: string) {
                if (!value || getFieldValue('newPassword') === value) {
                  return Promise.resolve();
                }
                return Promise.reject(new Error('两次输入的口令不一致'));
              },
            }),
          ]}
        >
          <Input.Password autoComplete="new-password" />
        </Form.Item>

        <Button type="primary" htmlType="submit" loading={pending}>
          修改并重新登录
        </Button>
      </Form>
    </Card>
  );
}
