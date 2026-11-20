import { tr } from '../../i18n/translate';
import { App, Form, InputNumber, Modal, Space, Tag, Typography } from 'antd';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { retentionApi } from '../api/retention';
import type { UpdateRetentionSettingsRequest } from '../api/types';

/** 两域清理宽限期：整体提交，避免只改一半。 */
export function RetentionSettingsModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<UpdateRetentionSettingsRequest>();

  const settings = useQuery({
    queryKey: ['retention', 'settings'],
    queryFn: ({ signal }) => retentionApi.getSettings(signal),
    enabled: open,
  });

  useEffect(() => {
    if (!open || !settings.data) {
      return;
    }
    form.setFieldsValue({
      backupsGraceMinutes: settings.data.backups.graceMinutes,
      documentsGraceMinutes: settings.data.documents.graceMinutes,
    });
  }, [open, settings.data, form]);

  const submit = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    try {
      await retentionApi.updateSettings(values);
      message.success(tr("清理宽限期已保存"));
      onClose();
      await queryClient.invalidateQueries({ queryKey: ['retention', 'settings'] });
      await queryClient.invalidateQueries({ queryKey: ['backups'] });
      await queryClient.invalidateQueries({ queryKey: ['documents'] });
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
    }
  };

  const sourceTag = (isDatabaseValue: boolean) =>
    isDatabaseValue ? <Tag color="blue">{tr("库内配置")}</Tag> : <Tag>{tr("出厂默认")}</Tag>;

  return (
    <Modal
      open={open}
      title={tr("清理宽限期")}
      okText={tr("保存")}
      cancelText={tr("取消")}
      confirmLoading={settings.isFetching}
      onOk={() => void submit()}
      onCancel={onClose}
    >
      <Typography.Paragraph type="secondary">
        {tr("软删除到物理清理之间的等待时间；宽限期内可在回收站恢复。修改作用于平台全部应用。")}
      </Typography.Paragraph>
      <Form form={form} layout="vertical">
        <Form.Item
          name="backupsGraceMinutes"
          label={
            <Space size={6}>
              {tr("备份")}
              {sourceTag(settings.data?.backups.isDatabaseValue ?? false)}
            </Space>
          }
          rules={[{ required: true, type: 'number', min: 1, message: tr("最小 1 分钟") }]}
        >
          <InputNumber min={1} max={10080} addonAfter={tr("分钟")} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item
          name="documentsGraceMinutes"
          label={
            <Space size={6}>
              {tr("文档")}
              {sourceTag(settings.data?.documents.isDatabaseValue ?? false)}
            </Space>
          }
          rules={[{ required: true, type: 'number', min: 1, message: tr("最小 1 分钟") }]}
        >
          <InputNumber min={1} max={10080} addonAfter={tr("分钟")} style={{ width: '100%' }} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
