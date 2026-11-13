import {
  BgColorsOutlined,
  LockOutlined,
  LogoutOutlined,
  MenuOutlined,
} from '@ant-design/icons';
import {
  Button,
  Drawer,
  Grid,
  Layout,
  Menu,
  Popover,
  Segmented,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { THEME_FAMILIES, THEME_MODES, isThemeMode, type ThemeFamily } from '../styles/antd-theme';
import { useAuthStore } from '../stores/auth';
import { selectedNavKey, visibleNavItems } from './navigation';
import { useTheme } from './theme-context';

const { Header, Sider, Content } = Layout;

/** 主题控件：宽屏直接摆在工具条上，窄屏收进气泡，两种尺寸都够点。 */
function ThemeControls({ stacked }: { stacked: boolean }) {
  const { family, mode, setFamily, setMode } = useTheme();

  return (
    <Space direction={stacked ? 'vertical' : 'horizontal'} size="small" wrap={!stacked}>
      <Select<ThemeFamily>
        size="small"
        value={family}
        onChange={setFamily}
        style={{ width: stacked ? 200 : 132 }}
        options={THEME_FAMILIES.map((item) => ({ value: item.key, label: item.label }))}
      />
      <Segmented
        size="small"
        block={stacked}
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
  );
}

/**
 * 管理台外壳。
 *
 * 宽屏是固定侧栏，窄屏（手机）侧栏收成抽屉：扫码审批本来就是手机上的活，
 * 216px 的常驻侧栏在 390px 的屏幕上会把内容挤成一条。导航按当前会话的权限裁剪。
 */
export function AppShell() {
  const navigate = useNavigate();
  const location = useLocation();
  const screens = Grid.useBreakpoint();
  const isMobile = !screens.md;
  const [navOpen, setNavOpen] = useState(false);
  const session = useAuthStore((state) => state.session);
  const signOut = useAuthStore((state) => state.signOut);

  const navItems = useMemo(() => visibleNavItems(session?.permissions), [session?.permissions]);

  // 换页即收抽屉：手机上不收的话新页面被遮罩盖着，看起来像「点了没反应」
  useEffect(() => {
    setNavOpen(false);
  }, [location.pathname]);

  const selectedKey = selectedNavKey(location.pathname);

  const handleSignOut = async () => {
    await signOut();
    navigate('/login', { replace: true });
  };

  const menu = (
    <Menu
      mode="inline"
      selectedKeys={[selectedKey]}
      onClick={({ key }) => navigate(key)}
      items={navItems.map((item) => ({ key: item.key, icon: item.icon, label: item.label }))}
    />
  );

  const brand = (
    <div className={isMobile ? 'app-shell__brand app-shell__brand--inline' : 'app-shell__brand'}>
      <Typography.Text strong>AppFramework</Typography.Text>
      {!isMobile ? <Typography.Text type="secondary">平台管理台</Typography.Text> : null}
    </div>
  );

  return (
    <Layout className="app-shell">
      {!isMobile ? (
        <Sider width={216} theme="light" className="app-shell__sider">
          {brand}
          {menu}
        </Sider>
      ) : null}

      <Drawer
        placement="left"
        width={244}
        open={isMobile && navOpen}
        onClose={() => setNavOpen(false)}
        title="平台管理台"
        styles={{ body: { padding: 0 } }}
      >
        {menu}
      </Drawer>

      <Layout>
        <Header className="app-shell__header">
          <Space size="small" align="center">
            {isMobile ? (
              <Button
                type="text"
                icon={<MenuOutlined />}
                aria-label="打开导航"
                onClick={() => setNavOpen(true)}
              />
            ) : null}
            {isMobile ? brand : null}
            {!isMobile ? <ThemeControls stacked={false} /> : null}
          </Space>

          <Space size="small" align="center">
            {isMobile ? (
              <Popover
                trigger="click"
                placement="bottomRight"
                title="主题"
                content={<ThemeControls stacked />}
              >
                <Button icon={<BgColorsOutlined />} aria-label="主题设置" />
              </Popover>
            ) : null}
            {session?.mustChangePassword ? <Tag color="warning">待改密</Tag> : null}
            {!isMobile ? (
              <Typography.Text>
                {session?.displayName ?? session?.userName ?? '未登录'}
              </Typography.Text>
            ) : null}
            <Button
              icon={<LockOutlined />}
              aria-label="修改口令"
              onClick={() => navigate('/password')}
            >
              {isMobile ? null : '修改口令'}
            </Button>
            <Button icon={<LogoutOutlined />} aria-label="退出" onClick={() => void handleSignOut()}>
              {isMobile ? null : '退出'}
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
