import i18n from './index';

/**
 * 以中文原文为键的翻译入口。
 *
 * 页面里的既有文案直接作为键：中文资源回填原文，英文资源按同一键给出译文。
 * 这样新增语言只需要补一份「中文 → 目标语言」字典，不会因为漏建语义键而回退成乱码。
 * 参数里的 `{{name}}` 由 i18next 插值。
 */
export function tr(text: string, options?: Record<string, unknown>): string {
  return i18n.t(text, {
    ns: 'ui',
    defaultValue: text,
    ...options,
  }) as string;
}
