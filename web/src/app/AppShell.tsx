import {
  BgColorsOutlined,
  LockOutlined,
  LogoutOutlined,
  MenuFoldOutlined,
  MenuOutlined,
  MenuUnfoldOutlined,
} from '@ant-design/icons';
import {
  Button,
  Drawer,
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
import { resolveNavLayout, SIDER_COLLAPSED_WIDTH, SIDER_WIDTH } from '../shared/responsive';
import { useResponsive } from '../shared/useResponsive';
import { THEME_FAMILIES, THEME_MODES, isThemeMode, type ThemeFamily } from '../styles/antd-theme';
import { useAuthStore } from '../stores/auth';
import { AppContextSelect } from './AppContextSelect';
import { selectedNavKey, visibleNavItems } from './navigation';
import { useTheme } from './theme-context';

const { Header, Sider, Content } = Layout;

/** 主题控件：收在气泡里，顶栏在 768/1024 都不会因为多一个控件二次换行。 */
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
 * 三档布局（与 `shared/responsive.ts` 同一口径）：
 * `<768` 侧栏抽屉；`768–1023` 折叠成 64px 图标栏；`≥1024` 216px 常驻。
 * 应用上下文切换统一放在 Header，页面不再各自维护「当前应用」卡片。
 */
export function AppShell() {
  const navigate = useNavigate();
  const location = useLocation();
  const { isMobile, isTablet, isDesktop } = useResponsive();
  const navLayout = resolveNavLayout({ isMobile, isTablet, isDesktop });
  const [navOpen, setNavOpen] = useState(false);
  const [navCollapsed, setNavCollapsed] = useState(false);
  const session = useAuthStore((state) => state.session);
  const signOut = useAuthStore((state) => state.signOut);

  const hasAppContext = (session?.appKeys?.length ?? 0) > 0;
  const navItems = useMemo(
    () => visibleNavItems(session?.permissions, { hasAppContext }),
    [session?.permissions, hasAppContext],
  );

  // 768–1023 默认收成图标栏；用户在本档内仍可手动展开，跨断点后再回到默认值。
  useEffect(() => {
    setNavCollapsed(isTablet);
  }, [isTablet]);

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
    <div className="app-shell__brand">
      <Typography.Text strong>AppFramework</Typography.Text>
      <Typography.Text type="secondary">平台管理台</Typography.Text>
    </div>
  );

  const collapsedBrand = (
    <div className="app-shell__brand app-shell__brand--collapsed" aria-label="AppFramework 平台管理台">
      <Typography.Text strong>AF</Typography.Text>
    </div>
  );

  const inlineBrand = (
    <div className="app-shell__brand app-shell__brand--inline">
      <Typography.Text strong>AppFramework</Typography.Text>
    </div>
  );

  const themeButton = (
    <Popover
      trigger="click"
      placement="bottomRight"
      title="主题"
      content={<ThemeControls stacked />}
    >
      <Button icon={<BgColorsOutlined />} aria-label="主题设置" />
    </Popover>
  );

  return (
    <Layout className="app-shell">
      {navLayout !== 'drawer' ? (
        <Sider
          width={SIDER_WIDTH}
          collapsedWidth={SIDER_COLLAPSED_WIDTH}
          collapsed={navCollapsed}
          trigger={null}
          theme="light"
          className="app-shell__sider"
        >
          {navCollapsed ? collapsedBrand : brand}
          {menu}
        </Sider>
      ) : null}

      <Drawer
        placement="left"
        width="86vw"
        className="app-shell__nav-drawer"
        open={isMobile && navOpen}
        onClose={() => setNavOpen(false)}
        title="平台管理台"
        styles={{ body: { padding: 0 } }}
      >
        {menu}
      </Drawer>

      <Layout>
        <Header className="app-shell__header">
          <Space size="small" align="center" wrap>
            {navLayout === 'drawer' ? (
              <Button
                type="text"
                icon={<MenuOutlined />}
                aria-label="打开导航"
                onClick={() => setNavOpen(true)}
              />
            ) : (
              <Button
                type="text"
                icon={navCollapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
                aria-label={navCollapsed ? '展开导航' : '收起导航'}
                onClick={() => setNavCollapsed((current) => !current)}
              />
            )}
            {isMobile ? inlineBrand : null}
            <AppContextSelect compact={!isDesktop} />
          </Space>

          <Space size="small" align="center">
            {themeButton}
            {session?.mustChangePassword ? <Tag color="warning">待改密</Tag> : null}
            {isDesktop ? (
              <Typography.Text>
                {session?.displayName ?? session?.userName ?? '未登录'}
              </Typography.Text>
            ) : null}
            <Button
              icon={<LockOutlined />}
              aria-label="修改口令"
              onClick={() => navigate('/password')}
            >
              {isDesktop ? '修改口令' : null}
            </Button>
            <Button icon={<LogoutOutlined />} aria-label="退出" onClick={() => void handleSignOut()}>
              {isDesktop ? '退出' : null}
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
