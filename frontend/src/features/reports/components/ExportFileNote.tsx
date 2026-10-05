import { Descriptions } from 'antd';
import { useTranslation } from 'react-i18next';
import { SUMMARY_LABEL_WIDTH } from '@/shared/components/layout';

/**
 * What the file will be written with and the user does not choose: the time zone of its times (the browser's, the zone
 * the same times are shown in on screen) and the language of its texts (the UI language at the moment of the request).
 */
export function ExportFileNote({ timeZone }: { timeZone: string }) {
  const { t } = useTranslation();

  return (
    <Descriptions
      size="small"
      column={1}
      styles={{ label: { width: SUMMARY_LABEL_WIDTH } }}
      style={{ marginBottom: 16 }}
      items={[
        { key: 'timeZone', label: t('reports.form.timeZone'), children: timeZone },
        { key: 'language', label: t('reports.form.language'), children: t('reports.form.languageValue') },
      ]}
    />
  );
}
