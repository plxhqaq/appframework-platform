import { Grid } from 'antd';
import { resolveResponsiveInfo, type ResponsiveInfo } from './responsive';

/** 页面统一从这里取断点，不要在组件里各自判断 `screens.md`。 */
export function useResponsive(): ResponsiveInfo {
  return resolveResponsiveInfo(Grid.useBreakpoint());
}
