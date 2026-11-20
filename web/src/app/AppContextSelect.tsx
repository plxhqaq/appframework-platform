import { AppstoreOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { App, Select, Tooltip } from 'antd';
import { useTranslation } from 'react-i18next';
import { useState } from 'react';
import { useAuthStore } from '../stores/auth';

interface AppContextSelectProps {
  /** 手机/折叠侧栏里再窄一档。 */
  compact?: boolean;
}

/**
 * 应用上下文切换器。
 *
 * 之前每个应用级页面各放一张「当前应用」卡片，位置和宽度都不一致；
 * 统一收到 Header 后，页面只依赖令牌里的 appKey，不再自己维护切换入口。
 */
export function AppContextSelect({ compact = false }: AppContextSelectProps) {
  const { t } = useTranslation();
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const switchAppContext = useAuthStore((state) => state.switchAppContext);
  const switching = useAuthStore((state) => state.pending);

  // Tooltip 与下拉面板都浮在 header 之上、Tooltip 层级更高，展开时会被提示条压住首行，
  // 所以下拉打开期间不让提示出现。
  const [hintOpen, setHintOpen] = useState(false);
  const [dropdownOpen, setDropdownOpen] = useState(false);

  const appKeys = session?.appKeys ?? [];
  if (appKeys.length === 0) {
    return null;
  }

  const current = session?.appKey ?? undefined;
  const disabled = switching || (appKeys.length <= 1 && current !== undefined);

  return (
    <Tooltip title={t('common:appContext.hint')} open={hintOpen && !dropdownOpen} onOpenChange={setHintOpen}>
      <Select
        // antd 会把选中项文本写进原生 title，和上面的 Tooltip 叠成两层提示；空串关掉原生提示。
        title=""
        size="small"
        aria-label={t('common:appContext.switch')}
        value={current}
        placeholder={t('common:appContext.switch')}
        loading={switching}
        disabled={disabled}
        style={{ width: compact ? 132 : 168, maxWidth: '100%', minWidth: 0 }}
        prefix={<AppstoreOutlined />}
        options={appKeys.map((key) => ({ value: key, label: key }))}
        onOpenChange={setDropdownOpen}
        onChange={async (next: string) => {
          if (next === current) {
            return;
          }

          try {
            const updated = await switchAppContext(next);
            await queryClient.invalidateQueries();
            message.success(t('common:messages.switchAppSuccess', { appKey: updated.appKey ?? next }));
          } catch (error) {
            message.error(error instanceof Error ? error.message : t('common:messages.switchAppFailed'));
          }
        }}
      />
    </Tooltip>
  );
}
