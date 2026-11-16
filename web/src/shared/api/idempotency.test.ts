import { afterEach, describe, expect, it, vi } from 'vitest';
import { idempotencyKey } from './idempotency';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

afterEach(() => vi.unstubAllGlobals());

describe('idempotencyKey', () => {
  it('安全上下文直接用 randomUUID', () => {
    expect(idempotencyKey()).toMatch(UUID_V4);
  });

  it('整站 http（非安全上下文没有 randomUUID）时回退 getRandomValues', () => {
    const real = globalThis.crypto;
    vi.stubGlobal('crypto', { getRandomValues: real.getRandomValues.bind(real) });
    expect(idempotencyKey()).toMatch(UUID_V4);
  });
});
