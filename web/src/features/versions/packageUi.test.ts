import { describe, expect, it } from 'vitest';
import {
  buildRegisterRequest,
  guessPackageFormat,
  manifestSourceLabel,
  mediaVersionLabel,
  packageKindMeta,
  packageStatusMeta,
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

  it('af-media 无版本时展示「未版本化」，有版本原样展示', () => {
    expect(mediaVersionLabel('')).toBe('未版本化');
    expect(mediaVersionLabel(null)).toBe('未版本化');
    expect(mediaVersionLabel('1.2.3')).toBe('1.2.3');
  });

  it('登记请求只声明字节与旁挂清单，且永远不进发布态', () => {
    const request = buildRegisterRequest({
      sha256: 'a'.repeat(64),
      sizeBytes: 1024,
      fileName: 'Setup-1.0.0.exe',
      manifestSha256: null,
    });

    expect(request).toEqual({
      sha256: 'a'.repeat(64),
      sizeBytes: 1024,
      fileName: 'Setup-1.0.0.exe',
      manifestSha256: null,
      publish: false,
    });
    expect(request.kind).toBeUndefined();
    expect(request.target).toBeUndefined();
    expect(request.channelKey).toBeUndefined();
    expect(request.version).toBeUndefined();
    expect(request.notes).toBeUndefined();
  });
});
