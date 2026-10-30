/* eslint-disable react-refresh/only-export-components -- 上下文对象与读取钩子必须同文件：上下文刻意不对外暴露，否则任何组件都能绕过主题白名单 */
import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import {
  DEFAULT_THEME,
  isThemeFamily,
  isThemeMode,
  type ThemeFamily,
  type ThemeMode,
} from '../styles/antd-theme';

/**
 * 主题状态：色系 + 模式，落 localStorage。
 *
 * 计划里的硬机制是"token 单一源"——这里只允许在 8 族 × 3 模式里选，
 * 任何自定义色值都必须先进 design/tokens.json 再重新生成。
 */
export interface ThemeContextValue {
  family: ThemeFamily;
  mode: ThemeMode;
  setFamily: (family: ThemeFamily) => void;
  setMode: (mode: ThemeMode) => void;
}

const EMPTY = '__theme_context_missing__';
const STORAGE_KEY = 'appframework.theme';

const ThemeContext = createContext<ThemeContextValue | typeof EMPTY>(EMPTY);

interface ThemeSelection {
  family: ThemeFamily;
  mode: ThemeMode;
}

function readStored(): ThemeSelection {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return { ...DEFAULT_THEME };
    }
    const parsed = JSON.parse(raw) as Partial<Record<keyof ThemeSelection, string>>;
    return {
      family: parsed.family && isThemeFamily(parsed.family) ? parsed.family : DEFAULT_THEME.family,
      mode: parsed.mode && isThemeMode(parsed.mode) ? parsed.mode : DEFAULT_THEME.mode,
    };
  } catch {
    return { ...DEFAULT_THEME };
  }
}

function persist(selection: ThemeSelection): ThemeSelection {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(selection));
  } catch {
    // 隐私模式下写不进去就算了，主题不影响功能
  }
  return selection;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [selection, setSelection] = useState<ThemeSelection>(readStored);

  const setFamily = useCallback((family: ThemeFamily) => {
    setSelection((previous) => persist({ ...previous, family }));
  }, []);

  const setMode = useCallback((mode: ThemeMode) => {
    setSelection((previous) => persist({ ...previous, mode }));
  }, []);

  const value = useMemo<ThemeContextValue>(
    () => ({ ...selection, setFamily, setMode }),
    [selection, setFamily, setMode],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

/** 读取当前主题选择。 */
export function useTheme(): ThemeContextValue {
  const value = useContext(ThemeContext);
  if (value === EMPTY) {
    throw new Error('useTheme 必须在 ThemeProvider 内使用');
  }
  return value;
}
