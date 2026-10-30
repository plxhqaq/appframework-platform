import { describe, expect, it } from 'vitest';
import {
  DEFAULT_THEME,
  getThemeTokens,
  isDarkMode,
  isThemeFamily,
  isThemeMode,
  THEME_FAMILIES,
  THEME_MODES,
  TOKENS_SHA256,
} from './antd-theme';

/**
 * 生成物守卫：这些断言是"token 单一源"的第二道闸。
 * 第一道是 tools/gen-tokens.mjs --check（逐字节比对），这一道保证内容本身的语义正确。
 */
describe('设计令牌生成物', () => {
  it('包含 8 个色系与 3 种模式', () => {
    expect(THEME_FAMILIES).toHaveLength(8);
    expect(THEME_MODES).toHaveLength(3);
    expect(THEME_MODES.map((mode) => mode.key)).toEqual(['light', 'dark', 'deepdark']);
  });

  it('记录源文件哈希', () => {
    expect(TOKENS_SHA256).toMatch(/^[0-9a-f]{64}$/);
  });

  it('24 套主题的主色两两不同（防止复制粘贴漏改）', () => {
    const primaries = new Set<string>();
    for (const family of THEME_FAMILIES) {
      for (const mode of THEME_MODES) {
        const token = getThemeTokens(family.key, mode.key);
        expect(token?.colorPrimary).toMatch(/^#[0-9a-f]{6}$/i);
        primaries.add(String(token?.colorPrimary));
      }
    }
    expect(primaries.size).toBe(24);
  });

  it('同一色系下三种模式的主色互不相同', () => {
    for (const family of THEME_FAMILIES) {
      const primaries = THEME_MODES.map((mode) => getThemeTokens(family.key, mode.key)?.colorPrimary);
      expect(new Set(primaries).size).toBe(3);
    }
  });

  it('暗色模式共享深底与浅字，浅色模式相反', () => {
    const { family } = DEFAULT_THEME;
    const light = getThemeTokens(family, 'light');
    const dark = getThemeTokens(family, 'dark');

    expect(light?.colorBgContainer).toBe('#ffffff');
    expect(String(light?.colorText)).toContain('rgba(0, 0, 0');
    expect(dark?.colorBgContainer).toBe('#141414');
    expect(String(dark?.colorText)).toContain('rgba(255, 255, 255');
  });

  it('深黑模式比深色更暗', () => {
    const { family } = DEFAULT_THEME;
    expect(getThemeTokens(family, 'deepdark')?.colorBgLayout).toBe('#0d0d0d');
  });

  it('模式与色系的类型守卫拒绝非法值', () => {
    expect(isDarkMode('dark')).toBe(true);
    expect(isDarkMode('deepdark')).toBe(true);
    expect(isDarkMode('light')).toBe(false);
    expect(isThemeMode('light')).toBe(true);
    expect(isThemeMode('neon')).toBe(false);
    expect(isThemeFamily('blue')).toBe(true);
    expect(isThemeFamily('neon')).toBe(false);
  });
});
