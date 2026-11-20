import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App as AntdApp, ConfigProvider, theme as antdTheme } from 'antd';
import enUS from 'antd/locale/en_US';
import zhCN from 'antd/locale/zh_CN';
import { useLayoutEffect, useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { getThemeTokens, isDarkMode } from '../styles/antd-theme';
import { AdminHubProvider } from '../shared/realtime/AdminHubConnection';
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

/**
 * 把 antd 解析后的 token 投影到 `:root` 上的 CSS 变量。
 *
 * 画布底色、外壳分隔线、滚动条这些表面不是 antd 组件，拿不到 ConfigProvider 的 token，
 * 只能走 CSS 变量；变量在这里由 token 生成，色值仍然只有 design/tokens.json 一个源。
 * 同时定下 `color-scheme`，让原生滚动条、输入控件与 `Canvas` 兜底色跟着明暗走。
 */
function ThemeSurface() {
  const { token } = antdTheme.useToken();
  const { mode } = useTheme();

  useLayoutEffect(() => {
    const root = document.documentElement;
    const surfaces: Record<string, string> = {
      '--app-bg-layout': token.colorBgLayout,
      '--app-bg-container': token.colorBgContainer,
      '--app-bg-elevated': token.colorBgElevated,
      '--app-text': token.colorText,
      '--app-text-secondary': token.colorTextSecondary,
      '--app-split': token.colorSplit,
      '--app-primary': token.colorPrimary,
      '--app-scrollbar-thumb': token.colorTextQuaternary,
      '--app-scrollbar-thumb-hover': token.colorTextTertiary,
    };

    for (const [name, value] of Object.entries(surfaces)) {
      root.style.setProperty(name, value);
    }

    root.style.colorScheme = isDarkMode(mode) ? 'dark' : 'light';
    root.dataset.themeMode = mode;
  }, [token, mode]);

  return null;
}

function ThemedShell({ children }: { children: ReactNode }) {
  const { family, mode } = useTheme();
  const { i18n } = useTranslation();

  // 每次渲染都新建 theme 对象会让 antd 重算整棵 token 树，按选择缓存住
  const theme = useMemo(
    () => ({
      algorithm: isDarkMode(mode) ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
      token: getThemeTokens(family, mode),
      components: {
        // 小号卡片默认 12px 内边距，标题栏的操作按钮几乎贴边；统一放宽到 20px
        Card: { bodyPaddingSM: 20, headerPaddingSM: 20 },
        // 浅色主题的气泡底色是 85% 半透，压在分隔线上会透出一行发丝缝，小尾巴看着像被边框隔开；
        // 换成同色值的实色（85% 黑压白 = #262626）观感不变但不再透光。深色/深黑本来就是实色。
        Tooltip: isDarkMode(mode) ? {} : { colorBgSpotlight: '#262626' },
      },
    }),
    [family, mode],
  );

  return (
    <ConfigProvider locale={i18n.resolvedLanguage === 'en-US' ? enUS : zhCN} theme={theme}>
      <ThemeSurface />
      {/* 页面里大量 tr() 取值在模块作用域之外、不订阅 i18n，切语言时整体重挂一次 */}
      <AntdApp key={i18n.resolvedLanguage}>{children}</AntdApp>
    </ConfigProvider>
  );
}

/** 全局 Provider 栈：服务端状态 → 实时事件通道 → 主题状态 → antd 主题与语言。 */
export function AppProviders({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient}>
      <AdminHubProvider>
        <ThemeProvider>
          <ThemedShell>{children}</ThemedShell>
        </ThemeProvider>
      </AdminHubProvider>
    </QueryClientProvider>
  );
}
