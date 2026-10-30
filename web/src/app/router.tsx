import { Spin } from 'antd';
import { useEffect, type ReactNode } from 'react';
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router-dom';
import { AppsPage } from '../features/apps/AppsPage';
import { ChangePasswordPage } from '../features/auth/ChangePasswordPage';
import { LoginPage } from '../features/auth/LoginPage';
import { OverviewPage } from '../features/dashboard/OverviewPage';
import { IdentityPage } from '../features/identity/IdentityPage';
import { VersionsPage } from '../features/versions/VersionsPage';
import { useAuthStore } from '../stores/auth';
import { AppShell } from './AppShell';

/**
 * 路由表。
 *
 * 权限过滤只挡 UI（计划明确：前端隐藏不算安全边界），
 * 每个写操作在服务端仍由 `RequirePermission` 二次强制。
 */
function RequireAuth({ children }: { children: ReactNode }) {
  const status = useAuthStore((state) => state.status);
  if (status !== 'authenticated') {
    return <Navigate to="/login" replace />;
  }
  return <>{children}</>;
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
      { path: 'overview', element: <OverviewPage /> },
      { path: 'apps', element: <AppsPage /> },
      { path: 'identity', element: <IdentityPage /> },
      { path: 'versions', element: <VersionsPage /> },
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
      <div style={{ minHeight: '100vh', display: 'grid', placeItems: 'center' }}>
        <Spin size="large" />
      </div>
    );
  }

  return <RouterProvider router={router} />;
}
