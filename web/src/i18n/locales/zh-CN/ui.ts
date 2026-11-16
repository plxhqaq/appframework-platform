import { uiEn } from '../en-US/ui';

/** 中文原文即键：中文资源把每个键原样回填，避免缺失时回退到英文。 */
export const uiZh = Object.fromEntries(Object.keys(uiEn).map((key) => [key, key]));
