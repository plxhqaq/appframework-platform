import {
  AppstoreOutlined,
  DashboardOutlined,
  DatabaseOutlined,
  FileTextOutlined,
  HistoryOutlined,
  KeyOutlined,
  LockOutlined,
  LogoutOutlined,
  SafetyOutlined,
  SwapOutlined,
} from '@ant-design/icons';
import { Button, Layout, Menu, Segmented, Select, Space, Tag, Tooltip, Typography } from 'antd';
import type { ReactNode } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { THEME_FAMILIES, THEME_MODES, isThemeMode, type ThemeFamily } from '../styles/antd-theme';
import { useAuthStore } from '../stores/auth';
import { useTheme } from './theme-context';

const { Header, Sider, Content } = Layout;

interface NavItem {
  key: string;
  icon: ReactNode;
  label: string;
  /** 未落地的页面先占位但禁用（比"画了跑不通"诚实）。 */
  ready: boolean;
}

const NAV_ITEMS: NavItem[] = [
  { key: '/overview', icon: <DashboardOutlined />, label: '总览', ready: true },
  { key: '/apps', icon: <AppstoreOutlined />, label: '应用接入', ready: true },
  { key: '/identity', icon: <SafetyOutlined />, label: '身份与权限', ready: true },
  { key: '/versions', icon: <SwapOutlined />, label: '版本与渠道', ready: true },
  { key: '/backups', icon: <DatabaseOutlined />, label: '配置备份', ready: true },
  { key: '/licensing', icon: <KeyOutlined />, label: '授权', ready: true },
  { key: '/documents', icon: <FileTextOutlined />, label: '文档', ready: true },
  { key: '/audit', icon: <HistoryOutlined />, label: '审计日志', ready: true },
];

/** 管理台外壳：侧栏导航 + 主题切换 + 当前主体。 */
export function AppShell() {
  const navigate = useNavigate();
  const location = useLocation();
  const { family, mode, setFamily, setMode } = useTheme();
  const session = useAuthStore((state) => state.session);
  const signOut = useAuthStore((state) => state.signOut);

  const selectedKey = NAV_ITEMS.find((item) => location.pathname.startsWith(item.key))?.key ?? '/overview';

  const handleSignOut = async () => {
    await signOut();
    navigate('/login', { replace: true });
  };

  return (
    <Layout className="app-shell">
      <Sider width={216} theme="light" className="app-shell__sider">
        <div className="app-shell__brand">
          <Typography.Text strong>AppFramework</Typography.Text>
          <Typography.Text type="secondary">平台管理台</Typography.Text>
        </div>
        <Menu
          mode="inline"
          selectedKeys={[selectedKey]}
          onClick={({ key }) => navigate(key)}
          items={NAV_ITEMS.map((item) => ({
            key: item.key,
            icon: item.icon,
            disabled: !item.ready,
            label: item.ready ? item.label : <Tooltip title="后续阶段接入">{item.label}</Tooltip>,
          }))}
        />
      </Sider>

      <Layout>
        <Header className="app-shell__header">
          <Space size="middle" wrap>
            <Select<ThemeFamily>
              size="small"
              value={family}
              onChange={setFamily}
              style={{ width: 132 }}
              options={THEME_FAMILIES.map((item) => ({ value: item.key, label: item.label }))}
            />
            <Segmented
              size="small"
              value={mode}
              onChange={(value) => {
                const next = String(value);
                if (isThemeMode(next)) {
                  setMode(next);
                }
              }}
              options={THEME_MODES.map((item) => ({ value: item.key, label: item.label }))}
            />
          </Space>

          <Space size="small">
            {session?.mustChangePassword ? <Tag color="warning">待改密</Tag> : null}
            <Typography.Text>{session?.displayName ?? session?.userName ?? '未登录'}</Typography.Text>
            <Button
              icon={<LockOutlined />}
              size="small"
              onClick={() => navigate('/password')}
            >
              修改口令
            </Button>
            <Button icon={<LogoutOutlined />} size="small" onClick={() => void handleSignOut()}>
              退出
            </Button>
          </Space>
        </Header>

        <Content className="app-shell__content">
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  );
}
