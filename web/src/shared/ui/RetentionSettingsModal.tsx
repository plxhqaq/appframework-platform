import { tr } from '../../i18n/translate';
import { Alert, App, Button, Form, InputNumber, Modal, Space, Tag, Typography } from 'antd';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { retentionApi } from '../api/retention';
import type { UpdateRetentionSettingsRequest } from '../api/types';

/** 两域清理宽限期：整体提交，避免只改一半。 */
export function RetentionSettingsModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<UpdateRetentionSettingsRequest>();
  // 只反映本次保存的在途状态；打开弹窗或后台重取不应让保存按钮转圈
  const [saving, setSaving] = useState(false);

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

    setSaving(true);
    try {
      await retentionApi.updateSettings(values);
      message.success(tr("清理宽限期已保存"));
      onClose();
      await queryClient.invalidateQueries({ queryKey: ['retention', 'settings'] });
      await queryClient.invalidateQueries({ queryKey: ['backups'] });
      await queryClient.invalidateQueries({ queryKey: ['documents'] });
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
    } finally {
      setSaving(false);
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
      confirmLoading={saving}
      okButtonProps={{ disabled: settings.isError || !settings.data }}
      onOk={() => void submit()}
      onCancel={onClose}
    >
      <Typography.Paragraph type="secondary">
        {tr("软删除到物理清理之间的等待时间；宽限期内可在回收站恢复。修改作用于平台全部应用。")}
      </Typography.Paragraph>
      {settings.isError ? (
        <Alert
          type="error"
          showIcon
          message={(settings.error as Error).message}
          action={
            <Button size="small" onClick={() => void settings.refetch()}>
              {tr("重试")}
            </Button>
          }
        />
      ) : null}
      <Form form={form} layout="vertical" style={{ marginTop: settings.isError ? 12 : 0 }}>
        <Form.Item
          name="backupsGraceMinutes"
          label={
            <Space size={6}>
              {tr("备份")}
              {/* 来源未知（加载中/失败）时不贴标签，避免把两域都误标成「出厂默认」 */}
              {settings.data ? sourceTag(settings.data.backups.isDatabaseValue) : null}
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
              {settings.data ? sourceTag(settings.data.documents.isDatabaseValue) : null}
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
