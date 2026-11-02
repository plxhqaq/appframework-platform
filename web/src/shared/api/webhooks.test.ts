import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { WEBHOOK_EVENT_TYPES, WEBHOOK_MAX_EVENT_TYPES } from './webhooks';

/**
 * 管理台的事件白名单是手写的（契约未稳定，与管理台其余类型同一口径），
 * 这里用服务端的 `WebhookConventions` 源码做漂移门禁：白名单变了却忘了改前端，
 * 用户在页面上选到的事件会在创建时被服务端拒掉。
 */
const contractsUrl = '../../../../src/Platform.Contracts/Webhooks/WebhookContracts.cs';
const contracts = readFileSync(fileURLToPath(new URL(contractsUrl, import.meta.url)), 'utf8');

/** 事件常量形如 `public const string EventTest = "webhook.test";`。 */
function serverEventTypes(): string[] {
  return [...contracts.matchAll(/public const string (Event[A-Za-z]+) = "([a-z]+\.[a-z]+)";/g)].map(
    (match) => match[2],
  );
}

describe('Webhook 事件白名单镜像', () => {
  it('与服务端 EventTypes 集合一致', () => {
    expect([...WEBHOOK_EVENT_TYPES.map((item) => item.value)].sort()).toEqual(
      serverEventTypes().sort(),
    );
  });

  it('值唯一且每项都有中文标签', () => {
    const values = WEBHOOK_EVENT_TYPES.map((item) => item.value);
    expect(new Set(values).size).toBe(values.length);
    WEBHOOK_EVENT_TYPES.forEach((item) => {
      expect(item.label.length).toBeGreaterThan(0);
    });
  });

  it('单条订阅事件数上限与服务端 MaxEventTypes 一致', () => {
    const matched = /public const int MaxEventTypes = (\d+);/.exec(contracts);
    expect(matched).not.toBeNull();
    expect(WEBHOOK_MAX_EVENT_TYPES).toBe(Number(matched?.[1]));
  });
});
