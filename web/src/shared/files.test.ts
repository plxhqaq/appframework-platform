import { describe, expect, it } from 'vitest';
import { hashFile, normalizeReleasePath, sha256ChunksHex, sha256Hex } from './files';

const encoder = new TextEncoder();

/** 确定性字节序列：同一 length 每次生成相同内容，便于断言分块一致性。 */
function bytes(length: number, seed = 0x5a): Uint8Array<ArrayBuffer> {
  const buffer = new Uint8Array(length);
  for (let index = 0; index < length; index += 1) {
    buffer[index] = (seed + index * 31) & 0xff;
  }

  return buffer;
}

describe('浏览器端文件工具', () => {
  it('计算已知内容的 SHA-256', async () => {
    const digest = await sha256Hex(encoder.encode('abc'));
    expect(digest).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  });

  it('流式哈希与 Web Crypto 在各填充边界上一致', async () => {
    // 55/56 是单块补齐的分界，63/64 是完整块分界，65 需要跨块；都覆盖一遍。
    for (const length of [0, 1, 55, 56, 63, 64, 65, 127, 128, 129, 1000]) {
      const data = bytes(length);
      const expected = await sha256Hex(data);
      expect(sha256ChunksHex([data])).toBe(expected);
    }
  });

  it('分块送入结果与整块一致', async () => {
    const data = bytes(5000);
    const expected = await sha256Hex(data);
    const splits = [
      [data.subarray(0, 64), data.subarray(64)],
      [data.subarray(0, 1), data.subarray(1, 63), data.subarray(63, 64), data.subarray(64)],
      [data.subarray(0, 55), data.subarray(55, 56), data.subarray(56)],
    ];

    for (const chunks of splits) {
      expect(sha256ChunksHex(chunks)).toBe(expected);
    }
  });

  it('hashFile 逐块读取并报告进度', async () => {
    const data = bytes(4 * 1024 * 1024 + 17);
    const file = new File([data], 'setup.exe');
    const progress: number[] = [];

    const digest = await hashFile(file, (hashed, total) => {
      expect(total).toBe(file.size);
      progress.push(hashed);
    });

    expect(digest).toBe(await sha256Hex(data));
    expect(progress).toEqual([4 * 1024 * 1024, file.size]);
  });

  it('hashFile 支持空文件', async () => {
    const progress: Array<[number, number]> = [];
    const digest = await hashFile(new File([], 'empty.bin'), (hashed, total) =>
      progress.push([hashed, total]),
    );

    expect(digest).toBe(await sha256Hex(new Uint8Array(0)));
    expect(progress).toEqual([[0, 0]]);
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
