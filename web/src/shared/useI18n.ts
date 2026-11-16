import { useTranslation } from 'react-i18next';
import '../i18n';

/** 组件内用当前语言渲染；副作用导入保证测试或单页入口也能拿到资源。 */
export function useI18n() {
  return useTranslation();
}
