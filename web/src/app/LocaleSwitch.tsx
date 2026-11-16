import { GlobalOutlined } from '@ant-design/icons';
import { Button, Popover, Select } from 'antd';
import { useTranslation } from 'react-i18next';
import { SUPPORTED_LOCALES, currentLocale, setLocale, type Locale } from '../i18n';

/** 界面语言切换：气泡里选语言，选择立即生效并写入 localStorage。 */
export function LocaleSwitch() {
  const { t } = useTranslation('common');
  const value = currentLocale();
  const active = SUPPORTED_LOCALES.find((item) => item.key === value) ?? SUPPORTED_LOCALES[0];
  const label = `${t('language')}: ${active.label}`;

  return (
    <Popover
      trigger="click"
      placement="bottomRight"
      title={t('language')}
      content={
        <Select<Locale>
          size="small"
          value={value}
          onChange={setLocale}
          style={{ width: 200 }}
          options={SUPPORTED_LOCALES.map((item) => ({ value: item.key, label: item.label }))}
        />
      }
    >
      <Button icon={<GlobalOutlined />} aria-label={label} title={label} />
    </Popover>
  );
}
