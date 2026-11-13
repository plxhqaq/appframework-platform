/**
 * 浏览器端文件哈希：上传票据要求先声明 SHA-256 与字节数，服务端上传后二次校验。
 *
 * `crypto.subtle.digest` 只能整块计算，GB 级安装包会按文件大小占内存；
 * `hashFile` 因此按块流式计算（纯 JS SHA-256），峰值内存固定为块大小。
 * 小内容仍走 `sha256Hex`（Web Crypto，快且经过平台实现验证）。
 */
export async function sha256Hex(data: BufferSource): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(digest))
    .map((byte) => byte.toString(16).padStart(2, '0'))
    .join('');
}

/** 流式哈希的分块大小：4 MB 在浏览器里兼顾吞吐与内存。 */
const HASH_CHUNK_BYTES = 4 * 1024 * 1024;

/** 哈希进度回调（已处理字节 / 总字节）。 */
export type HashProgressHandler = (hashed: number, total: number) => void;

/** 计算文件哈希；返回小写十六进制摘要，按块读取避免整包进内存。 */
export async function hashFile(file: File, onProgress?: HashProgressHandler): Promise<string> {
  const hasher = new Sha256();
  for (let offset = 0; offset < file.size; offset += HASH_CHUNK_BYTES) {
    const end = Math.min(offset + HASH_CHUNK_BYTES, file.size);
    const chunk = await file.slice(offset, end).arrayBuffer();
    hasher.update(new Uint8Array(chunk));
    onProgress?.(end, file.size);
  }

  if (file.size === 0) {
    onProgress?.(0, 0);
  }

  return hasher.digestHex();
}

/** 按给定分块流式计算 SHA-256（诊断与单元测试用；生产路径走 <see cref="hashFile"/>）。 */
export function sha256ChunksHex(chunks: Uint8Array[]): string {
  const hasher = new Sha256();
  for (const chunk of chunks) {
    hasher.update(chunk);
  }

  return hasher.digestHex();
}

const SHA256_K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

function rotr(value: number, bits: number): number {
  return ((value >>> bits) | (value << (32 - bits))) >>> 0;
}

/** 增量 SHA-256（FIPS 180-4）；只为流式读取文件，不替代 Web Crypto。 */
class Sha256 {
  private readonly state = new Uint32Array([
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
  ]);

  private readonly block = new Uint8Array(64);
  private readonly words = new Uint32Array(64);
  private blockLength = 0;
  private totalBytes = 0;

  update(chunk: Uint8Array): void {
    this.totalBytes += chunk.length;
    let offset = 0;

    if (this.blockLength > 0) {
      const needed = Math.min(64 - this.blockLength, chunk.length);
      this.block.set(chunk.subarray(0, needed), this.blockLength);
      this.blockLength += needed;
      offset = needed;
      if (this.blockLength === 64) {
        this.compress(this.block, 0);
        this.blockLength = 0;
      }
    }

    while (offset + 64 <= chunk.length) {
      this.compress(chunk, offset);
      offset += 64;
    }

    if (offset < chunk.length) {
      this.block.set(chunk.subarray(offset), 0);
      this.blockLength = chunk.length - offset;
    }
  }

  digestHex(): string {
    const tail = new Uint8Array(this.blockLength < 56 ? 64 : 128);
    tail.set(this.block.subarray(0, this.blockLength), 0);
    tail[this.blockLength] = 0x80;

    // 64 位比特长度：文件上限 4 GiB（2^35 bit），用 Number 计算仍在安全整数范围内。
    const bitLengthHigh = Math.floor(this.totalBytes / 0x20000000);
    const bitLengthLow = (this.totalBytes % 0x20000000) * 8;
    const lengthOffset = tail.length - 8;
    tail[lengthOffset] = (bitLengthHigh >>> 24) & 0xff;
    tail[lengthOffset + 1] = (bitLengthHigh >>> 16) & 0xff;
    tail[lengthOffset + 2] = (bitLengthHigh >>> 8) & 0xff;
    tail[lengthOffset + 3] = bitLengthHigh & 0xff;
    tail[lengthOffset + 4] = (bitLengthLow >>> 24) & 0xff;
    tail[lengthOffset + 5] = (bitLengthLow >>> 16) & 0xff;
    tail[lengthOffset + 6] = (bitLengthLow >>> 8) & 0xff;
    tail[lengthOffset + 7] = bitLengthLow & 0xff;

    for (let offset = 0; offset < tail.length; offset += 64) {
      this.compress(tail, offset);
    }

    let hex = '';
    for (const value of this.state) {
      hex += value.toString(16).padStart(8, '0');
    }

    return hex;
  }

  private compress(chunk: Uint8Array, offset: number): void {
    const w = this.words;
    for (let index = 0; index < 16; index += 1) {
      const start = offset + index * 4;
      w[index] =
        ((chunk[start] << 24) |
          (chunk[start + 1] << 16) |
          (chunk[start + 2] << 8) |
          chunk[start + 3]) >>>
        0;
    }

    for (let index = 16; index < 64; index += 1) {
      const previous15 = w[index - 15];
      const previous2 = w[index - 2];
      const sigma0 = (rotr(previous15, 7) ^ rotr(previous15, 18) ^ (previous15 >>> 3)) >>> 0;
      const sigma1 = (rotr(previous2, 17) ^ rotr(previous2, 19) ^ (previous2 >>> 10)) >>> 0;
      w[index] = (w[index - 16] + sigma0 + w[index - 7] + sigma1) >>> 0;
    }

    const state = this.state;
    let a = state[0];
    let b = state[1];
    let c = state[2];
    let d = state[3];
    let e = state[4];
    let f = state[5];
    let g = state[6];
    let h = state[7];

    for (let index = 0; index < 64; index += 1) {
      const sum1 = (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) >>> 0;
      const choice = ((e & f) ^ (~e & g)) >>> 0;
      const temp1 = (h + sum1 + choice + SHA256_K[index] + w[index]) >>> 0;
      const sum0 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) >>> 0;
      const majority = ((a & b) ^ (a & c) ^ (b & c)) >>> 0;
      const temp2 = (sum0 + majority) >>> 0;

      h = g;
      g = f;
      f = e;
      e = (d + temp1) >>> 0;
      d = c;
      c = b;
      b = a;
      a = (temp1 + temp2) >>> 0;
    }

    state[0] = (state[0] + a) >>> 0;
    state[1] = (state[1] + b) >>> 0;
    state[2] = (state[2] + c) >>> 0;
    state[3] = (state[3] + d) >>> 0;
    state[4] = (state[4] + e) >>> 0;
    state[5] = (state[5] + f) >>> 0;
    state[6] = (state[6] + g) >>> 0;
    state[7] = (state[7] + h) >>> 0;
  }
}

/**
 * 规范清单路径：Windows 分隔符转 `/`，去掉目录选择的根目录段。
 * 与服务端 `NormalizePath` 的口径一致——空段、`.`、`..`、绝对路径都会被拒绝。
 */
export function normalizeReleasePath(rawPath: string, stripRoot: boolean): string {
  const normalized = rawPath.trim().replace(/\\/g, '/');
  if (!stripRoot) {
    return normalized;
  }

  const index = normalized.indexOf('/');
  return index >= 0 ? normalized.slice(index + 1) : normalized;
}
