import type { ReactNode } from 'react';
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router-dom';
import { AppsPage } from '../features/apps/AppsPage';
import { LoginPage } from '../features/auth/LoginPage';
import { OverviewPage } from '../features/dashboard/OverviewPage';
import { useAuthStore } from '../stores/auth';
import { AppShell } from './AppShell';

/**
 * 路由表。
 *
 * 权限过滤只挡 UI（计划明确：前端隐藏不算安全边界），
 * 每个写操作在服务端仍由 `RequirePermission` 二次强制。
 */
function RequireAuth({ children }: { children: ReactNode }) {
  const accessToken = useAuthStore((state) => state.accessToken);
  if (!accessToken) {
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
    ],
  },
  { path: '*', element: <Navigate to="/overview" replace /> },
]);

export function AppRouter() {
  return <RouterProvider router={router} />;
}
