import { describe, expect, it } from 'vitest';
import { canRedeliver, DELIVERY_STATUS_META, formatPayload } from './webhookUi';

describe('Webhook 台账展示逻辑', () => {
  it('只有失败重试中与死信能手动重投', () => {
    expect(canRedeliver('failed')).toBe(true);
    expect(canRedeliver('dead')).toBe(true);
    expect(canRedeliver('pending')).toBe(false);
    expect(canRedeliver('succeeded')).toBe(false);
  });

  it('四个投递状态都有中文标签', () => {
    expect(Object.keys(DELIVERY_STATUS_META).sort()).toEqual(
      ['dead', 'failed', 'pending', 'succeeded'].sort(),
    );
    Object.values(DELIVERY_STATUS_META).forEach((meta) => {
      expect(meta.label).not.toBe('');
      expect(meta.color).not.toBe('');
    });
  });

  it('合法 JSON 按缩进展开', () => {
    expect(formatPayload('{"a":1,"b":[2]}')).toBe('{\n  "a": 1,\n  "b": [\n    2\n  ]\n}');
  });

  it('解析失败时退回原始字节（截断的负载也能看清）', () => {
    expect(formatPayload('{"a":1')).toBe('{"a":1');
    expect(formatPayload('')).toBe('');
  });
});
