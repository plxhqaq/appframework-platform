import { tr } from '../../i18n/translate';
import { useQuery } from '@tanstack/react-query';
import { Alert, App, Checkbox, Modal, Space, Table, Tag, Typography } from 'antd';
import type { TableColumnsType } from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { versionsApi } from '../../shared/api/versions';
import type { DriverMedia, ReleasePackage } from '../../shared/api/types';
import { startDownload } from '../../shared/download';
import { formatBytes } from '../../shared/format';
import { mediaVersionLabel } from './packageUi';

interface BundleDownloadModalProps {
  /** 目标全量发布包；为 null 时不渲染内容。 */
  releasePackage: ReleasePackage | null;
  onClose: () => void;
}

/**
 * 下载完整包：全量安装包 + 勾选的启用驱动媒体合成一个确定性 zip。
 * 默认勾选按包清单 mediaRequirements 命中（mediaKey 对齐 componentId）；
 * 声明项没有启用媒体时须显式「忽略缺失项继续」，服务端会记审计。
 */
export function BundleDownloadModal({ releasePackage, onClose }: BundleDownloadModalProps) {
  const { message } = App.useApp();
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [allowMissing, setAllowMissing] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const mediaQuery = useQuery({
    queryKey: ['versions', 'media', 'bundle-candidates', releasePackage?.id],
    queryFn: ({ signal }) =>
      versionsApi.listMedia({ page: 1, pageSize: 200, activeOnly: true }, signal),
    enabled: releasePackage !== null,
  });

  const candidates = useMemo(() => mediaQuery.data?.items ?? [], [mediaQuery.data]);

  const declaredKeys = useMemo(() => {
    const reqs = releasePackage?.mediaRequirements ?? [];
    return reqs.map((req) => req.componentId.trim().toLowerCase());
  }, [releasePackage]);

  // 清单到位后按声明预勾选（候选清空时同步清空，防已删媒体残留）；用户之后的手动增删不被轮询覆盖
  useEffect(() => {
    if (!releasePackage) {
      return;
    }
    const declared = new Set(declaredKeys);
    setSelectedIds(
      candidates.filter((row) => declared.has(row.mediaKey.toLowerCase())).map((row) => row.id),
    );
    setAllowMissing(false);
  }, [releasePackage, candidates, declaredKeys]);

  const matchedKeys = useMemo(
    () => new Set(candidates.map((row) => row.mediaKey.toLowerCase())),
    [candidates],
  );
  const missingDeclared = useMemo(
    () => declaredKeys.filter((key) => !matchedKeys.has(key)),
    [declaredKeys, matchedKeys],
  );

  const confirm = async () => {
    if (!releasePackage) {
      return;
    }
    setSubmitting(true);
    try {
      const ticket = await versionsApi.createBundleTicket(releasePackage.id, {
        mediaIds: selectedIds,
        allowMissing: missingDeclared.length > 0 ? allowMissing : undefined,
      });
      startDownload(ticket.url);
      message.success(
        tr("已开始下载完整包（{{arg0}} 个媒体）", { arg0: String(selectedIds.length) }),
      );
      onClose();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("生成下载票据失败"));
    } finally {
      setSubmitting(false);
    }
  };

  const columns: TableColumnsType<DriverMedia> = [
    {
      title: tr("媒体键"),
      dataIndex: 'mediaKey',
      key: 'mediaKey',
      width: 140,
      render: (key: string) => {
        const declared = declaredKeys.includes(key.trim().toLowerCase());
        return (
          <Space size={4}>
            <Typography.Text code>{key}</Typography.Text>
            {declared ? <Tag color="blue">{tr("声明")}</Tag> : null}
          </Space>
        );
      },
    },
    {
      title: tr("版本"),
      dataIndex: 'version',
      key: 'version',
      width: 100,
      render: (value: string) => mediaVersionLabel(value),
    },
    { title: tr("名称"), dataIndex: 'name', key: 'name', width: 150, responsive: ['md'] },
    {
      title: tr("大小"),
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      render: (size: number) => formatBytes(size),
    },
    { title: tr("文件数"), dataIndex: 'fileCount', key: 'fileCount', width: 80, responsive: ['md'] },
  ];

  const open = releasePackage !== null;
  const totalSize = candidates
    .filter((row) => selectedIds.includes(row.id))
    .reduce((sum, row) => sum + row.sizeBytes, 0);

  return (
    <Modal
      open={open}
      width={680}
      title={tr("下载完整包")}
      okText={tr("生成并下载")}
      cancelText={tr("取消")}
      okButtonProps={{
        disabled: mediaQuery.isPending || (missingDeclared.length > 0 && !allowMissing),
        loading: submitting,
      }}
      maskClosable={!submitting}
      onCancel={submitting ? undefined : onClose}
      onOk={() => void confirm()}
      destroyOnClose
    >
      {releasePackage ? (
        <>
          <Typography.Paragraph type="secondary">
            {tr("全量安装包 {{arg0}}（{{arg1}}）将与服务端合成 zip；首次组合需现场打包，可能等待较久。", {
              arg0: releasePackage.fileName,
              arg1: releasePackage.version,
            })}
          </Typography.Paragraph>

          {missingDeclared.length > 0 ? (
            <Alert
              type="warning"
              showIcon
              style={{ marginBottom: 12 }}
              message={tr("以下声明的媒体组件没有启用版本：{{arg0}}", { arg0: missingDeclared.join(', ') })}
              description={
                <Checkbox
                  checked={allowMissing}
                  onChange={(event) => setAllowMissing(event.target.checked)}
                >
                  {tr("忽略缺失项继续下载（服务端将记录审计）")}</Checkbox>
              }
            />
          ) : null}

          {mediaQuery.isError ? (
            <Alert
              type="error"
              showIcon
              style={{ marginBottom: 12 }}
              message={(mediaQuery.error as Error).message}
            />
          ) : null}

          {declaredKeys.length === 0 && candidates.length > 0 ? (
            <Alert
              type="info"
              showIcon
              style={{ marginBottom: 12 }}
              message={tr("该包清单未声明媒体需求，可自由勾选；不勾选则仅打包安装程序本体。")}
            />
          ) : null}

          <Table<DriverMedia>
            rowKey="id"
            size="small"
            loading={mediaQuery.isPending}
            dataSource={candidates}
            columns={columns}
            pagination={false}
            rowSelection={{
              selectedRowKeys: selectedIds,
              onChange: (keys) => setSelectedIds(keys.map(String)),
            }}
            locale={{ emptyText: tr("当前没有已启用的驱动媒体包；可先在「驱动媒体」面板上传。") }}
          />

          {selectedIds.length > 0 ? (
            <Typography.Text type="secondary">
              {tr("已选 {{count}} 个媒体 · 媒体合计 {{size}}", {
                count: String(selectedIds.length),
                size: formatBytes(totalSize),
              })}
            </Typography.Text>
          ) : null}
        </>
      ) : null}
    </Modal>
  );
}
