import { describe, expect, it } from 'vitest';
import {
  buildRegisterRequest,
  guessPackageFormat,
  manifestSourceLabel,
  packageKindMeta,
  packageStatusMeta,
  packageTargetError,
  safeDownloadName,
} from './packageUi';

describe('发布包展示口径', () => {
  it('已知类型与状态映射为中文标签', () => {
    expect(packageKindMeta('full').label).toBe('全量安装包');
    expect(packageKindMeta('incremental').label).toBe('增量包');
    expect(packageStatusMeta('pending').label).toBe('待摄取');
    expect(packageStatusMeta('ready').label).toBe('可用');
    expect(packageStatusMeta('failed').color).toBe('red');
  });

  it('未知取值原样展示，不抛错', () => {
    expect(packageStatusMeta('uploading').label).toBe('uploading');
    expect(packageKindMeta('delta').label).toBe('delta');
  });

  it('清单来源覆盖五种协议', () => {
    expect(manifestSourceLabel('embedded')).toBe('PE 内嵌');
    expect(manifestSourceLabel('zip')).toBe('包内 JSON');
    expect(manifestSourceLabel('sidecar')).toBe('旁挂 JSON');
    expect(manifestSourceLabel('manual')).toBe('人工登记');
    expect(manifestSourceLabel('legacy')).toBe('旧版协议');
  });

  it('按扩展名推断格式且忽略大小写', () => {
    expect(guessPackageFormat('Setup-App-1.0.0.EXE')).toBe('exe');
    expect(guessPackageFormat('update.zip')).toBe('zip');
    expect(guessPackageFormat('archive.tar.gz')).toBeNull();
    expect(guessPackageFormat('noext')).toBeNull();
  });

  it('增量包必须绑定具体运行时', () => {
    expect(packageTargetError('incremental', 'any')).toBe('增量包必须指定具体的运行时标识');
    expect(packageTargetError('incremental', 'win-x64')).toBeNull();
    expect(packageTargetError('full', 'any')).toBeNull();
  });

  it('登记请求把空白字段归一为 null', () => {
    const request = buildRegisterRequest({
      sha256: 'a'.repeat(64),
      sizeBytes: 1024,
      fileName: 'Setup-1.0.0.exe',
      kind: 'full',
      target: 'any',
      channelKey: '  ',
      version: ' 1.0.0 ',
      notes: '   ',
      manifestSha256: null,
      publish: true,
      canPublish: true,
    });

    expect(request).toMatchObject({
      sha256: 'a'.repeat(64),
      sizeBytes: 1024,
      fileName: 'Setup-1.0.0.exe',
      kind: 'full',
      target: 'any',
      channelKey: null,
      version: '1.0.0',
      notes: null,
      manifestSha256: null,
      publish: true,
    });
  });

  it('无发布权限时强制保留草稿', () => {
    const request = buildRegisterRequest({
      sha256: 'b'.repeat(64),
      sizeBytes: 2048,
      fileName: 'Setup-1.0.0.msi',
      kind: 'full',
      target: 'any',
      publish: true,
      canPublish: false,
    });

    expect(request.publish).toBe(false);
  });

  it('下载文件名去掉路径与危险字符', () => {
    expect(safeDownloadName('..\\evil/Setup.exe')).toBe('.._evil_Setup.exe');
    expect(safeDownloadName('Setup.exe')).toBe('Setup.exe');
    expect(safeDownloadName('')).toBe('package');
  });
});
