import { Spin } from 'antd';
import { useEffect, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { createBrowserRouter, Navigate, RouterProvider, useLocation } from 'react-router-dom';
import { AppsPage } from '../features/apps/AppsPage';
import { AuditPage } from '../features/audit/AuditPage';
import { BackupsPage } from '../features/backups/BackupsPage';
import { ChangePasswordPage } from '../features/auth/ChangePasswordPage';
import { DocumentsPage } from '../features/documents/DocumentsPage';
import { InstallationsPage } from '../features/installations/InstallationsPage';
import { LoginPage } from '../features/auth/LoginPage';
import { NodesPage } from '../features/nodes/NodesPage';
import { OverviewPage } from '../features/dashboard/OverviewPage';
import { IdentityPage } from '../features/identity/IdentityPage';
import { QrApprovalPage } from '../features/identity/QrApprovalPage';
import { QrSessionDetailPage } from '../features/identity/QrSessionDetailPage';
import { SingleKeyPage } from '../features/identity/SingleKeyPage';
import { LicensingPage } from '../features/licensing/LicensingPage';
import { UpdatersPage } from '../features/updaters/UpdatersPage';
import { VersionsPage } from '../features/versions/VersionsPage';
import { WebhooksPage } from '../features/webhooks/WebhooksPage';
import { PERMISSIONS, hasAnyPermissionCode } from '../shared/permissions';
import { loginPath } from '../shared/returnTo';
import { useAuthStore } from '../stores/auth';
import { AccessDenied } from './AccessDenied';
import { AppShell } from './AppShell';
import { requiredPermissions } from './navigation';

/**
 * 路由表。
 *
 * 权限过滤只挡 UI（计划明确：前端隐藏不算安全边界），
 * 每个写操作在服务端仍由 `RequirePermission` 二次强制。
 * 但「导航藏了、手输地址还能进」是另一回事：那会让人落到一个整页报错的页面上，
 * 所以路由这一层也按同一份权限表判一次。
 */
function RequireAuth({ children }: { children: ReactNode }) {
  const status = useAuthStore((state) => state.status);
  const location = useLocation();

  if (status !== 'authenticated') {
    // 带上原目标：手机扫码直达审批页时，登录完要回到那一单，而不是被扔到总览
    return <Navigate to={loginPath(location.pathname + location.search)} replace />;
  }
  return <>{children}</>;
}

function Guarded({ path, children }: { path: string; children: ReactNode }) {
  const permissions = useAuthStore((state) => state.session?.permissions);
  if (!hasAnyPermissionCode(permissions, requiredPermissions(path))) {
    return <AccessDenied />;
  }
  return <>{children}</>;
}

function GuardedQrDetail() {
  const { t } = useTranslation('auth');
  const permissions = useAuthStore((state) => state.session?.permissions);
  if (!hasAnyPermissionCode(permissions, [PERMISSIONS.identityQrApprove])) {
    return (
      <AccessDenied description={t('denied.qrDescription')} />
    );
  }
  return <QrSessionDetailPage />;
}

const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  {
    path: '/',
    element: (
      <RequireAuth>
        <AppShell />
      </RequireAuth>
    ),
    children: [
      { index: true, element: <Navigate to="/overview" replace /> },
      { path: 'overview', element: <Guarded path="/overview"><OverviewPage /></Guarded> },
      { path: 'single-key', element: <Guarded path="/single-key"><SingleKeyPage /></Guarded> },
      { path: 'qr-approval', element: <Guarded path="/qr-approval"><QrApprovalPage /></Guarded> },
      { path: 'qr-login/:requestId', element: <GuardedQrDetail /> },
      { path: 'apps', element: <Guarded path="/apps"><AppsPage /></Guarded> },
      { path: 'nodes', element: <Guarded path="/nodes"><NodesPage /></Guarded> },
      { path: 'identity', element: <Guarded path="/identity"><IdentityPage /></Guarded> },
      { path: 'versions', element: <Guarded path="/versions"><VersionsPage /></Guarded> },
      { path: 'updaters', element: <Guarded path="/updaters"><UpdatersPage /></Guarded> },
      { path: 'installations', element: <Guarded path="/installations"><InstallationsPage /></Guarded> },
      { path: 'backups', element: <Guarded path="/backups"><BackupsPage /></Guarded> },
      { path: 'documents', element: <Guarded path="/documents"><DocumentsPage /></Guarded> },
      { path: 'licensing', element: <Guarded path="/licensing"><LicensingPage /></Guarded> },
      { path: 'webhooks', element: <Guarded path="/webhooks"><WebhooksPage /></Guarded> },
      { path: 'audit', element: <Guarded path="/audit"><AuditPage /></Guarded> },
      { path: 'password', element: <ChangePasswordPage /> },
    ],
  },
  { path: '*', element: <Navigate to="/overview" replace /> },
]);

export function AppRouter() {
  const status = useAuthStore((state) => state.status);
  const bootstrap = useAuthStore((state) => state.bootstrap);

  useEffect(() => {
    void bootstrap();
  }, [bootstrap]);

  // 首屏先尝试用 httpOnly Cookie 静默续期，避免已登录用户闪一下登录页
  if (status === 'unknown') {
    return (
      <div className="app-boot">
        <Spin size="large" />
      </div>
    );
  }

  return <RouterProvider router={router} />;
}
