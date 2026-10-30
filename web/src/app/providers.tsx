import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App as AntdApp, ConfigProvider, theme as antdTheme } from 'antd';
import zhCN from 'antd/locale/zh_CN';
import type { ReactNode } from 'react';
import { getThemeTokens, isDarkMode } from '../styles/antd-theme';
import { ThemeProvider, useTheme } from './theme-context';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      refetchOnWindowFocus: false,
      staleTime: 30_000,
    },
  },
});

function ThemedShell({ children }: { children: ReactNode }) {
  const { family, mode } = useTheme();

  return (
    <ConfigProvider
      locale={zhCN}
      theme={{
        algorithm: isDarkMode(mode) ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
        token: getThemeTokens(family, mode),
      }}
    >
      <AntdApp>{children}</AntdApp>
    </ConfigProvider>
  );
}

/** 全局 Provider 栈：服务端状态 → 主题状态 → antd 主题与语言。 */
export function AppProviders({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider>
        <ThemedShell>{children}</ThemedShell>
      </ThemeProvider>
    </QueryClientProvider>
  );
}
