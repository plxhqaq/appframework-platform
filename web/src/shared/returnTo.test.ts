import { describe, expect, it } from 'vitest';
import { loginPath, safeReturnTo } from './returnTo';

describe('登录后回跳目标', () => {
  it('站内路径原样保留，查询串一起带上', () => {
    expect(safeReturnTo('/qr-approval')).toBe('/qr-approval');
    expect(safeReturnTo('/qr-login/9f2c?x=1')).toBe('/qr-login/9f2c?x=1');
  });

  it('站外地址与协议相对地址一律丢弃', () => {
    expect(safeReturnTo('https://evil.example.com')).toBeNull();
    expect(safeReturnTo('//evil.example.com')).toBeNull();
    expect(safeReturnTo('/\\evil.example.com')).toBeNull();
    expect(safeReturnTo('qr-approval')).toBeNull();
    expect(safeReturnTo('')).toBeNull();
    expect(safeReturnTo(null)).toBeNull();
    expect(safeReturnTo(undefined)).toBeNull();
  });

  it('回跳到登录页自己会绕圈，也要丢弃', () => {
    expect(safeReturnTo('/login')).toBeNull();
    expect(safeReturnTo('/login?returnTo=%2Foverview')).toBeNull();
  });

  it('拼进登录路径时做转义，没有目标就是裸登录页', () => {
    expect(loginPath('/qr-login/abc')).toBe('/login?returnTo=%2Fqr-login%2Fabc');
    expect(loginPath('https://evil.example.com')).toBe('/login');
  });
});
