import { describe, expect, it } from 'vitest';
import {
  resolveNavLayout,
  resolveResponsiveInfo,
  responsivePagination,
  responsiveWidth,
} from './responsive';

describe('断点判定', () => {
  it('360 宽：md 未命中，按移动端处理', () => {
    const info = resolveResponsiveInfo({});
    expect(info).toEqual({ isMobile: true, isTablet: false, isDesktop: false });
    expect(resolveNavLayout(info)).toBe('drawer');
  });

  it('768 宽：md 命中但 lg 未命中，侧栏折叠成图标栏', () => {
    const info = resolveResponsiveInfo({ sm: true, md: true });
    expect(info).toEqual({ isMobile: false, isTablet: true, isDesktop: false });
    expect(resolveNavLayout(info)).toBe('icon');
  });

  it('1024 与 1440 宽：lg 命中，常驻侧栏', () => {
    for (const screens of [{ md: true, lg: true }, { sm: true, md: true, lg: true, xl: true }]) {
      const info = resolveResponsiveInfo(screens);
      expect(info).toEqual({ isMobile: false, isTablet: false, isDesktop: true });
      expect(resolveNavLayout(info)).toBe('full');
    }
  });

  it('断点字段为 undefined 时按未命中处理，不抛错', () => {
    expect(resolveResponsiveInfo({ sm: undefined, md: undefined }).isMobile).toBe(true);
  });
});

describe('弹窗/抽屉宽度', () => {
  it('移动端整宽，桌面保留设计宽度', () => {
    expect(responsiveWidth(true, 760)).toBe('100%');
    expect(responsiveWidth(false, 760)).toBe(760);
  });
});

describe('分页形态', () => {
  const options = {
    current: 2,
    pageSize: 20,
    total: 45,
    showTotal: (total: number) => `共 ${total} 条`,
  };

  it('移动端用 simple，隐藏 size changer 与总数', () => {
    const result = responsivePagination(true, options);
    expect(result.simple).toBe(true);
    expect(result.showSizeChanger).toBe(false);
    expect(result.showTotal).toBeUndefined();
    expect(result.current).toBe(2);
    expect(result.total).toBe(45);
  });

  it('桌面端保留 size changer 与 showTotal', () => {
    const result = responsivePagination(false, options);
    expect(result.simple).toBe(false);
    expect(result.showSizeChanger).toBe(true);
    expect(result.showTotal?.(45)).toBe('共 45 条');
  });

  it('不改动传入对象的其余字段', () => {
    const result = responsivePagination(false, { ...options, onChange: undefined });
    expect(Object.keys(result)).toEqual(
      expect.arrayContaining(['current', 'pageSize', 'total', 'onChange']),
    );
  });
});
