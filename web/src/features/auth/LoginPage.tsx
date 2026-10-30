import { LockOutlined, UserOutlined } from '@ant-design/icons';
import { Alert, App, Button, Card, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { isDevTokenMode } from '../../shared/api/auth';
import { useAuthStore } from '../../stores/auth';

interface LoginFormValues {
  userName: string;
  password: string;
}

/**
 * 登录页。
 *
 * S0 通过开发令牌端点打通「登录 → 拿令牌 → whoami → 进入壳子」这条链路；
 * S1 落地 `/api/v1/identity/login` 后把 `VITE_USE_DEV_TOKEN` 置为 false 即可，
 * 密码框与错误提示逻辑不需要改。
 */
export function LoginPage() {
  const navigate = useNavigate();
  const { message } = App.useApp();
  const signIn = useAuthStore((state) => state.signIn);
  const pending = useAuthStore((state) => state.pending);
  const [error, setError] = useState<string | null>(null);
  const devMode = isDevTokenMode();

  const handleFinish = async (values: LoginFormValues) => {
    setError(null);
    try {
      await signIn(values.userName, values.password ?? '');
      navigate('/overview', { replace: true });
    } catch (caught) {
      const text = caught instanceof Error ? caught.message : '登录失败';
      setError(text);
      message.error(text);
    }
  };

  return (
    <div className="login-page">
      <Card className="login-page__card">
        <Typography.Title level={3} style={{ marginBottom: 4 }}>
          AppFramework 平台
        </Typography.Title>
        <Typography.Paragraph type="secondary">通用服务提供平台 · 管理台</Typography.Paragraph>

        {devMode ? (
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 16 }}
            message="当前为开发令牌登录"
            description="身份服务在 S1 接入；此模式仅在 API 处于 Development 且开启 Jwt:AllowDevTokenEndpoint 时可用。"
          />
        ) : null}

        {error ? <Alert type="error" showIcon style={{ marginBottom: 16 }} message={error} /> : null}

        <Form<LoginFormValues>
          layout="vertical"
          requiredMark={false}
          initialValues={{ userName: 'admin', password: '' }}
          onFinish={handleFinish}
        >
          <Form.Item name="userName" label="用户名" rules={[{ required: true, message: '请输入用户名' }]}>
            <Input prefix={<UserOutlined />} placeholder="用户名" autoComplete="username" />
          </Form.Item>

          <Form.Item name="password" label="密码">
            <Input.Password
              prefix={<LockOutlined />}
              placeholder={devMode ? '开发令牌登录无需密码' : '密码'}
              disabled={devMode}
              autoComplete="current-password"
            />
          </Form.Item>

          <Button type="primary" htmlType="submit" block loading={pending}>
            登录
          </Button>
        </Form>
      </Card>
    </div>
  );
}
