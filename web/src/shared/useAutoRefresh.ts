import { useState } from 'react';
import { tr } from '../i18n/translate';

export const DEFAULT_INTERVAL_SECONDS = 60;
export const MIN_INTERVAL_SECONDS = 1;
export const MAX_INTERVAL_SECONDS = 3600;

export const clampIntervalSeconds = (value: number) =>
  Math.min(MAX_INTERVAL_SECONDS, Math.max(MIN_INTERVAL_SECONDS, Math.round(value)));

/**
 * 页面级兜底轮询开关：状态跃迁以 AdminHub 事件失效回拉为主，轮询只是事件缺失时的补偿，
 * 所以默认关闭，进页面不自带后台请求。
 */
export function useAutoRefresh(defaultIntervalSeconds = DEFAULT_INTERVAL_SECONDS) {
  const [enabled, setEnabled] = useState(false);
  const [intervalSeconds, setIntervalSeconds] = useState(clampIntervalSeconds(defaultIntervalSeconds));
  const refetchInterval: number | false = enabled ? intervalSeconds * 1000 : false;

  return {
    enabled,
    setEnabled,
    intervalSeconds,
    setIntervalSeconds,
    refetchInterval,
    label: enabled ? tr("每 {{arg0}} 秒自动刷新", { arg0: intervalSeconds }) : tr("自动刷新已暂停"),
  };
}

export type AutoRefresh = ReturnType<typeof useAutoRefresh>;
