import { LockOutlined, UserOutlined } from '@ant-design/icons';
import { Alert, App, Button, Card, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { HOME_PATH } from '../../app/navigation';
import { safeReturnTo } from '../../shared/returnTo';
import { useAuthStore } from '../../stores/auth';

interface LoginFormValues {
  userName: string;
  password: string;
}

/**
 * 管理端登录：口令校验通过后刷新令牌由服务端写入 httpOnly Cookie。
 *
 * 带 returnTo 时登录完回到原目标——手机扫开的审批链接会先落到这里，
 * 登录完必须回到那一单，而不是被扔到总览让现场再找一遍。
 */
export function LoginPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const { message } = App.useApp();
  const signIn = useAuthStore((state) => state.signIn);
  const status = useAuthStore((state) => state.status);
  const pending = useAuthStore((state) => state.pending);
  const [error, setError] = useState<string | null>(null);

  const returnTo = safeReturnTo(searchParams.get('returnTo')) ?? HOME_PATH;

  if (status === 'authenticated') {
    return <Navigate to={returnTo} replace />;
  }

  const handleFinish = async (values: LoginFormValues) => {
    setError(null);
    try {
      const session = await signIn(values.userName, values.password);
      if (session.mustChangePassword) {
        message.warning('首次登录或口令已被重置，请先修改口令');
        navigate(
          returnTo === HOME_PATH
            ? '/password'
            : `/password?returnTo=${encodeURIComponent(returnTo)}`,
          { replace: true },
        );
        return;
      }
      navigate(returnTo, { replace: true });
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

        {returnTo !== HOME_PATH ? (
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 16 }}
            message="登录后会回到你刚才打开的页面"
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
