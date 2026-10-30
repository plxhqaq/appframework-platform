import { describe, expect, it } from 'vitest';
import { normalizeReleasePath, sha256Hex } from './files';

describe('浏览器端文件工具', () => {
  it('计算已知内容的 SHA-256', async () => {
    const digest = await sha256Hex(new TextEncoder().encode('abc'));
    expect(digest).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  });

  it('反斜杠统一为正斜杠', () => {
    expect(normalizeReleasePath('App\\lib\\core.dll', false)).toBe('App/lib/core.dll');
  });

  it('目录选择时去掉根目录段', () => {
    expect(normalizeReleasePath('MyApp/App.exe', true)).toBe('App.exe');
    expect(normalizeReleasePath('MyApp/lib/core.dll', true)).toBe('lib/core.dll');
  });

  it('单文件选择不做根目录裁剪', () => {
    expect(normalizeReleasePath('App.exe', true)).toBe('App.exe');
  });
});
