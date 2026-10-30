import { LockOutlined, UserOutlined } from '@ant-design/icons';
import { Alert, App, Button, Card, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { useAuthStore } from '../../stores/auth';

interface LoginFormValues {
  userName: string;
  password: string;
}

/** 管理端登录：口令校验通过后刷新令牌由服务端写入 httpOnly Cookie。 */
export function LoginPage() {
  const navigate = useNavigate();
  const { message } = App.useApp();
  const signIn = useAuthStore((state) => state.signIn);
  const status = useAuthStore((state) => state.status);
  const pending = useAuthStore((state) => state.pending);
  const [error, setError] = useState<string | null>(null);

  if (status === 'authenticated') {
    return <Navigate to="/overview" replace />;
  }

  const handleFinish = async (values: LoginFormValues) => {
    setError(null);
    try {
      const session = await signIn(values.userName, values.password);
      if (session.mustChangePassword) {
        message.warning('首次登录或口令已被重置，请先修改口令');
        navigate('/password', { replace: true });
        return;
      }
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

          <Form.Item
            name="password"
            label="密码"
            rules={[{ required: true, message: '请输入密码' }]}
          >
            <Input.Password
              prefix={<LockOutlined />}
              placeholder="密码"
              autoComplete="current-password"
            />
          </Form.Item>

          <Button type="primary" htmlType="submit" block loading={pending}>
            登录
          </Button>
        </Form>

        <Typography.Paragraph type="secondary" style={{ marginTop: 16, marginBottom: 0, fontSize: 12 }}>
          初始管理员口令由部署环境变量 <Typography.Text code>Seed__AdminPassword</Typography.Text> 注入，
          首次登录后必须修改。
        </Typography.Paragraph>
      </Card>
    </div>
  );
}
