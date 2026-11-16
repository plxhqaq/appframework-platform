import { tr } from '../../i18n/translate';
import { Alert, App, Button, Card, Col, Form, Input, Row, Space } from 'antd';
import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { PageHeading } from '../../app/PageHeading';
import { authApi } from '../../shared/api/auth';
import { loginPath, safeReturnTo } from '../../shared/returnTo';
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
  const [searchParams] = useSearchParams();
  const { message } = App.useApp();
  const session = useAuthStore((state) => state.session);
  const signOut = useAuthStore((state) => state.signOut);
  const [form] = Form.useForm<ChangePasswordValues>();
  const [pending, setPending] = useState(false);

  const handleFinish = async (values: ChangePasswordValues) => {
    setPending(true);
    try {
      const result = await authApi.changePassword(values.currentPassword, values.newPassword);
      message.success(tr("口令已修改，已吊销 {{arg0}} 个会话，请重新登录", { arg0: result.revokedSessions }));
      await signOut();
      // 扫码直达时先被要求改密的，改完重新登录还要回到那一单
      navigate(loginPath(safeReturnTo(searchParams.get('returnTo'))), { replace: true });
    } catch (caught) {
      message.error(caught instanceof Error ? caught.message : tr("修改口令失败"));
    } finally {
      setPending(false);
    }
  };

  return (
    <div className="field-page field-page--wide">
      <PageHeading
        title={tr("修改口令")}
        description={
          session?.mustChangePassword
            ? tr("当前账号要求先修改初始口令，修改后全部已登录设备都需要重新登录。")
            : tr("修改后全部已登录设备都需要重新登录。")
        }
      />

      <Row gutter={[16, 16]}>
        <Col xs={24} xl={14}>
          <Card size="small" title={tr("口令变更")}>
            <Form<ChangePasswordValues>
              form={form}
              layout="vertical"
              requiredMark={false}
              style={{ maxWidth: 640 }}
              onFinish={handleFinish}
            >
              <Form.Item
                name="currentPassword"
                label={tr("当前口令")}
                rules={[{ required: true, message: tr("请输入当前口令") }]}
              >
                <Input.Password autoComplete="current-password" />
              </Form.Item>

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
                name="confirmPassword"
                label={tr("确认新口令")}
                dependencies={['newPassword']}
                rules={[
                  { required: true, message: tr("请再次输入新口令") },
                  ({ getFieldValue }) => ({
                    validator(_, value: string) {
                      if (!value || getFieldValue('newPassword') === value) {
                        return Promise.resolve();
                      }
                      return Promise.reject(new Error(tr("两次输入的口令不一致")));
                    },
                  }),
                ]}
              >
                <Input.Password autoComplete="new-password" />
              </Form.Item>

              <Button type="primary" htmlType="submit" loading={pending}>
                {tr("修改并重新登录")}</Button>
            </Form>
          </Card>
        </Col>

        <Col xs={24} xl={10}>
          <Space direction="vertical" size="middle" style={{ width: '100%' }}>
            <Alert
              type="warning"
              showIcon
              message={tr("改密后全部设备需重新登录")}
              description={tr("服务端会吊销该账号的全部刷新令牌，其他设备上正在使用的会话随即失效。")}
            />
            <Alert
              type="info"
              showIcon
              message={tr("口令规则")}
              description={tr("8–128 位，且同时包含字母与数字。")}
            />
          </Space>
        </Col>
      </Row>
    </div>
  );
}
