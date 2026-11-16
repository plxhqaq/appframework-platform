import { tr } from '../../i18n/translate';
import type { WebhookDeliveryStatus } from '../../shared/api/types';

/** 投递状态的展示口径（与服务端 `WebhookConventions.Status*` 一一对应）。 */
export const DELIVERY_STATUS_META: Record<WebhookDeliveryStatus, { label: string; color: string }> = {
  pending: { get label() { return tr("待投递"); }, color: 'blue' },
  failed: { get label() { return tr("失败重试中"); }, color: 'orange' },
  succeeded: { get label() { return tr("已送达"); }, color: 'green' },
  dead: { get label() { return tr("死信"); }, color: 'red' },
};

/** 只有失败与死信能手动重投；服务端对其它状态直接拒绝。 */
export function canRedeliver(status: WebhookDeliveryStatus): boolean {
  return status === 'failed' || status === 'dead';
}

/** 负载原样展示，解析失败就退回原文——排查签名问题要看的是投递时的那串字节。 */
export function formatPayload(payloadJson: string): string {
  try {
    return JSON.stringify(JSON.parse(payloadJson), null, 2);
  } catch {
    return payloadJson;
  }
}
