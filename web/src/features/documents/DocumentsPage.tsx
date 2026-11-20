import { tr } from '../../i18n/translate';
import { PlusOutlined, ReloadOutlined, UploadOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Card,
  Descriptions,
  Drawer,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Upload,
  Typography,
} from 'antd';
import type { TableColumnsType, UploadFile } from 'antd';
import { useEffect, useState } from 'react';
import { requestBlob } from '../../shared/api/client';
import { documentsApi } from '../../shared/api/documents';
import type {
  CreateDocumentCollectionRequest,
  DocumentCollection,
  DocumentDetail,
  DocumentSummary,
  DocumentVisibility,
  UpdateDocumentCollectionRequest,
  UpdateDocumentRequest,
} from '../../shared/api/types';
import { saveBlob } from '../../shared/download';
import { formatBytes, formatDateTime } from '../../shared/format';
import { PERMISSIONS } from '../../shared/permissions';
import { responsivePagination, responsiveWidth } from '../../shared/responsive';
import { useResponsive } from '../../shared/useResponsive';
import { useAuthStore } from '../../stores/auth';

interface CollectionFormValues {
  key: string;
  name: string;
  description?: string;
  isEnabled: boolean;
}

interface UploadFormValues {
  collectionKey: string;
  title?: string;
  visibility: DocumentVisibility;
  applicableVersion?: string;
}

interface EditFormValues {
  title: string;
  visibility: DocumentVisibility;
  applicableVersion?: string;
}

const DEFAULT_PAGE_SIZE = 20;

const VISIBILITY_META: Record<DocumentVisibility, { label: string; color?: string }> = {
  client: { get label() { return tr("客户端可见"); }, color: 'green' },
  internal: { get label() { return tr("仅内部"); } },
};

/**
 * 文档管理页。
 *
 * 默认可见性为 internal；只有显式放开为 client 且集合启用时，客户端清单才会出现。
 * 服务端在清单与下载两处都强制过滤，前端不承担安全边界。
 */
export function DocumentsPage() {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const session = useAuthStore((state) => state.session);
  const canManageCollection = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.documentCollectionManage),
  );
  const canManage = useAuthStore((state) => state.hasPermission(PERMISSIONS.documentManage));
  const canDelete = useAuthStore((state) => state.hasPermission(PERMISSIONS.documentDelete));

  const appKeys = session?.appKeys ?? [];
  const appKey = session?.appKey ?? appKeys[0] ?? null;

  const [collectionForm] = Form.useForm<CollectionFormValues>();
  const [uploadForm] = Form.useForm<UploadFormValues>();
  const [editForm] = Form.useForm<EditFormValues>();

  const [collectionOpen, setCollectionOpen] = useState(false);
  const [editingCollection, setEditingCollection] = useState<DocumentCollection | null>(null);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [uploadFiles, setUploadFiles] = useState<UploadFile[]>([]);
  const [revisionTarget, setRevisionTarget] = useState<DocumentSummary | null>(null);
  const [revisionFiles, setRevisionFiles] = useState<UploadFile[]>([]);
  const [editTarget, setEditTarget] = useState<DocumentSummary | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [collectionFilter, setCollectionFilter] = useState<string | undefined>();
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [saving, setSaving] = useState(false);

  // 切换应用上下文在 Header 完成；旧应用的集合适配与详情抽屉要一起作废
  useEffect(() => {
    setCollectionFilter(undefined);
    setPage(1);
    setDetailId(null);
  }, [appKey]);

  const collections = useQuery({
    queryKey: ['documents', 'collections', appKey],
    queryFn: ({ signal }) => documentsApi.listCollections(signal),
    enabled: appKey !== null,
  });

  const documents = useQuery({
    queryKey: ['documents', 'list', appKey, collectionFilter, page, pageSize],
    queryFn: ({ signal }) =>
      documentsApi.listDocuments(
        { collectionKey: collectionFilter, page, pageSize },
        signal,
      ),
    enabled: appKey !== null,
  });

  const detail = useQuery({
    queryKey: ['documents', 'detail', detailId],
    queryFn: ({ signal }) => documentsApi.getDocument(detailId!, signal),
    enabled: detailId !== null,
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['documents'] });
  };

  const openCollectionForm = (collection: DocumentCollection | null) => {
    setEditingCollection(collection);
    collectionForm.resetFields();
    collectionForm.setFieldsValue({
      key: collection?.key ?? '',
      name: collection?.name ?? '',
      description: collection?.description ?? '',
      isEnabled: collection?.isEnabled ?? true,
    });
    setCollectionOpen(true);
  };

  const submitCollection = async () => {
    const values = await collectionForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    setSaving(true);
    try {
      if (editingCollection) {
        const body: UpdateDocumentCollectionRequest = {
          name: values.name,
          description: values.description ?? null,
          isEnabled: values.isEnabled,
        };
        await documentsApi.updateCollection(editingCollection.id, body);
        message.success(tr("集合已更新"));
      } else {
        const body: CreateDocumentCollectionRequest = {
          key: values.key,
          name: values.name,
          description: values.description ?? null,
        };
        await documentsApi.createCollection(body);
        message.success(tr("集合已创建"));
      }

      setCollectionOpen(false);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
    } finally {
      setSaving(false);
    }
  };

  const openUploadForm = () => {
    const first = (collections.data ?? []).find((item) => item.isEnabled);
    uploadForm.resetFields();
    uploadForm.setFieldsValue({
      collectionKey: first?.key ?? '',
      title: '',
      visibility: 'internal',
      applicableVersion: '',
    });
    setUploadFiles([]);
    setUploadOpen(true);
  };

  const submitUpload = async () => {
    const values = await uploadForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    const file = uploadFiles[0]?.originFileObj;
    if (!file) {
      message.warning(tr("请选择要上传的文件"));
      return;
    }

    setSaving(true);
    try {
      await documentsApi.uploadDocument(
        values.collectionKey,
        file,
        values.title?.trim() || undefined,
        values.visibility,
        values.applicableVersion?.trim() || undefined,
      );
      message.success(tr("文档已上传"));
      setUploadOpen(false);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("上传失败"));
    } finally {
      setSaving(false);
    }
  };

  const openRevisionForm = (row: DocumentSummary) => {
    setRevisionTarget(row);
    setRevisionFiles([]);
  };

  const submitRevision = async () => {
    if (!revisionTarget) {
      return;
    }

    const file = revisionFiles[0]?.originFileObj;
    if (!file) {
      message.warning(tr("请选择要上传的文件"));
      return;
    }

    setSaving(true);
    try {
      await documentsApi.addRevision(revisionTarget.id, file);
      message.success(tr("已生成新修订"));
      setRevisionTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("上传修订失败"));
    } finally {
      setSaving(false);
    }
  };

  const openEditForm = (row: DocumentSummary) => {
    setEditTarget(row);
    editForm.resetFields();
    editForm.setFieldsValue({
      title: row.title,
      visibility: row.visibility,
      applicableVersion: row.applicableVersion ?? '',
    });
  };

  const submitEdit = async () => {
    if (!editTarget) {
      return;
    }

    const values = await editForm.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    setSaving(true);
    try {
      const body: UpdateDocumentRequest = {
        title: values.title,
        visibility: values.visibility,
        // 空串表示清空；null 会被服务端视为「不修改」
        applicableVersion: values.applicableVersion?.trim() ?? '',
      };
      await documentsApi.updateDocument(editTarget.id, body);
      message.success(tr("文档已更新"));
      setEditTarget(null);
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("更新失败"));
    } finally {
      setSaving(false);
    }
  };

  const handleDownload = async (url: string, fileName: string) => {
    try {
      const blob = await requestBlob(url);
      saveBlob(blob, fileName);
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("下载失败"));
    }
  };

  const handleDelete = (row: DocumentSummary) => {
    Modal.confirm({
      title: tr("删除文档「{{arg0}}」？", { arg0: row.title }),
      content: tr("客户端清单会立即移除；文件与修订在宽限期后由后台清理。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          await documentsApi.deleteDocument(row.id);
          message.success(tr("文档已删除"));
          setDetailId(null);
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("删除失败"));
        }
      },
    });
  };

  const collectionColumns: TableColumnsType<DocumentCollection> = [
    {
      title: tr("键"),
      dataIndex: 'key',
      key: 'key',
      width: 140,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    { title: tr("名称"), dataIndex: 'name', key: 'name', width: 160 },
    {
      title: tr("说明"),
      dataIndex: 'description',
      key: 'description',
      render: (value: string | null) => value ?? <Typography.Text type="secondary">-</Typography.Text>,
    },
    { title: tr("文档数"), dataIndex: 'documentCount', key: 'documentCount', width: 80 },
    {
      title: tr("状态"),
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 80,
      render: (enabled: boolean) => (enabled ? <Tag color="green">{tr("启用")}</Tag> : <Tag>{tr("停用")}</Tag>),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 130,
      fixed: 'right',
      render: (_: unknown, row: DocumentCollection) =>
        canManageCollection ? (
          <Space size="small">
            <Button type="link" size="small" onClick={() => openCollectionForm(row)}>
              {tr("编辑")}
            </Button>
            <Button
              type="link"
              size="small"
              danger
              onClick={() => confirmDeleteCollection(row)}
            >
              {tr("删除")}
            </Button>
          </Space>
        ) : null,
    },
  ];

  const deleteCollectionRow = async (collection: DocumentCollection) => {
    try {
      await documentsApi.deleteCollection(collection.id);
      message.success(tr("集合 {{arg0}} 已删除", { arg0: collection.key }));
      await refresh();
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("操作失败"));
    }
  };

  const confirmDeleteCollection = (collection: DocumentCollection) => {
    modal.confirm({
      title: tr("删除文档集合 {{arg0}}？", { arg0: collection.key }),
      content: tr("集合内仍有文档（含软删除待清理）时会被拒绝，以保留文档留痕。此操作不可恢复。"),
      okText: tr("删除"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: () => deleteCollectionRow(collection),
    });
  };

  const documentColumns: TableColumnsType<DocumentSummary> = [
    { title: tr("集合"), dataIndex: 'collectionKey', key: 'collectionKey', width: 120 },
    { title: tr("标题"), dataIndex: 'title', key: 'title', width: 220 },
    {
      title: tr("可见性"),
      dataIndex: 'visibility',
      key: 'visibility',
      width: 110,
      render: (visibility: DocumentVisibility) => {
        const meta = VISIBILITY_META[visibility];
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: tr("适用版本"),
      dataIndex: 'applicableVersion',
      key: 'applicableVersion',
      width: 110,
      responsive: ['md'],
      render: (value: string | null) => value ?? '-',
    },
    { title: tr("修订"), dataIndex: 'revision', key: 'revision', width: 70 },
    {
      title: tr("文件名"),
      dataIndex: 'fileName',
      key: 'fileName',
      width: 180,
      responsive: ['md'],
    },
    {
      title: tr("大小"),
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      responsive: ['md'],
      render: (value: number) => formatBytes(value),
    },
    {
      title: tr("更新时间"),
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      width: 170,
      responsive: ['md'],
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 250,
      fixed: 'right',
      render: (_: unknown, row: DocumentSummary) => (
        <Space size="small">
          <Button type="link" size="small" onClick={() => setDetailId(row.id)}>
            {tr("详情")}</Button>
          {canManage ? (
            <Button type="link" size="small" onClick={() => openRevisionForm(row)}>
              {tr("新修订")}</Button>
          ) : null}
          {canManage ? (
            <Button type="link" size="small" onClick={() => openEditForm(row)}>
              {tr("编辑")}</Button>
          ) : null}
          {canDelete ? (
            <Button type="link" size="small" danger onClick={() => handleDelete(row)}>
              {tr("删除")}</Button>
          ) : null}
        </Space>
      ),
    },
  ];

  const revisionColumns: TableColumnsType<DocumentDetail['revisions'][number]> = [
    { title: tr("修订"), dataIndex: 'revision', key: 'revision', width: 70 },
    { title: tr("文件名"), dataIndex: 'fileName', key: 'fileName' },
    {
      title: tr("大小"),
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      render: (value: number) => formatBytes(value),
    },
    {
      title: tr("上传时间"),
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      responsive: ['md'],
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 80,
      fixed: 'right',
      render: (_: unknown, row: DocumentDetail['revisions'][number]) => (
        <Button
          type="link"
          size="small"
          onClick={() => void handleDownload(row.downloadUrl, row.fileName)}
        >
          {tr("下载")}</Button>
      ),
    },
  ];

  if (appKey === null) {
    return (
      <Card title={tr("文档")} size="small">
        <Alert
          type="warning"
          showIcon
          message={tr("当前账号没有可用的应用上下文")}
          description={tr("文档按应用隔离。请先在「应用接入」注册应用，并使用拥有该应用访问权的账号进入。")}
        />
      </Card>
    );
  }

  return (
    <Space direction="vertical" size={16} style={{ display: 'flex' }}>
      <Card
        size="small"
        title={tr("集合")}
        extra={
          canManageCollection ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => openCollectionForm(null)}
            >
              {tr("新建集合")}</Button>
          ) : null
        }
      >
        {collections.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(collections.error as Error).message}
            action={
              <Button size="small" onClick={() => void collections.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<DocumentCollection>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={collections.isPending}
          dataSource={collections.data ?? []}
          columns={collectionColumns}
          pagination={false}
          locale={{ emptyText: tr("尚未创建集合（如 manual / api / changelog）") }}
        />
      </Card>

      <Card
        size="small"
        title={tr("文档")}
        extra={
          canManage ? (
            <Button type="primary" icon={<PlusOutlined />} onClick={openUploadForm}>
              {tr("上传文档")}</Button>
          ) : null
        }
      >
        <Space wrap style={{ marginBottom: 12 }}>
          <Select
            allowClear
            placeholder={tr("全部集合")}
            style={{ width: 200, maxWidth: '100%' }}
            value={collectionFilter}
            onChange={(value) => {
              setCollectionFilter(value);
              setPage(1);
            }}
            options={(collections.data ?? []).map((item) => ({
              value: item.key,
              label: `${item.name}（${item.key}）`,
            }))}
          />
          <Button icon={<ReloadOutlined />} onClick={() => void refresh()}>
            {tr("刷新")}</Button>
        </Space>

        {documents.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={(documents.error as Error).message}
            action={
              <Button size="small" onClick={() => void documents.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        <Table<DocumentSummary>
          scroll={{ x: 'max-content' }}
          rowKey="id"
          size="small"
          loading={documents.isPending}
          dataSource={documents.data?.items ?? []}
          columns={documentColumns}
          pagination={responsivePagination(isMobile, {
            current: documents.data?.page ?? page,
            pageSize: documents.data?.pageSize ?? pageSize,
            total: documents.data?.total ?? 0,
            showTotal: (total: number) => tr("共 {{arg0}} 篇文档", { arg0: total }),
            onChange: (nextPage: number, nextPageSize: number) => {
              setPage(nextPageSize === pageSize ? nextPage : 1);
              setPageSize(nextPageSize);
            },
          })}
          locale={{ emptyText: tr("尚无文档") }}
        />
      </Card>

      <Modal
        open={collectionOpen}
        title={editingCollection ? tr("编辑集合") : tr("新建集合")}
        confirmLoading={saving}
        onOk={() => void submitCollection()}
        onCancel={() => setCollectionOpen(false)}
      >
        <Form form={collectionForm} layout="vertical">
          <Form.Item
            name="key"
            label={tr("键")}
            rules={[{ required: true, pattern: /^[a-z][a-z0-9-]{1,31}$/, message: tr("小写字母开头，2–32 位小写字母/数字/连字符") }]}
          >
            <Input disabled={editingCollection !== null} placeholder="manual" />
          </Form.Item>
          <Form.Item name="name" label={tr("名称")} rules={[{ required: true, max: 256 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="description" label={tr("说明")}>
            <Input.TextArea rows={2} />
          </Form.Item>
          {editingCollection ? (
            <Form.Item name="isEnabled" label={tr("启用")} valuePropName="checked">
              <Switch />
            </Form.Item>
          ) : null}
        </Form>
      </Modal>

      <Modal
        open={uploadOpen}
        title={tr("上传文档")}
        confirmLoading={saving}
        onOk={() => void submitUpload()}
        onCancel={() => setUploadOpen(false)}
      >
        <Form form={uploadForm} layout="vertical">
          <Form.Item name="collectionKey" label={tr("集合")} rules={[{ required: true }]}>
            <Select
              options={(collections.data ?? [])
                .filter((item) => item.isEnabled)
                .map((item) => ({ value: item.key, label: `${item.name}（${item.key}）` }))}
            />
          </Form.Item>
          <Form.Item name="title" label={tr("标题（缺省用文件名）")}>
            <Input />
          </Form.Item>
          <Form.Item name="visibility" label={tr("可见性")} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'internal', label: tr("仅内部（默认）") },
                { value: 'client', label: tr("客户端可见") },
              ]}
            />
          </Form.Item>
          <Form.Item
            name="applicableVersion"
            label={tr("适用版本（可选）")}
            rules={[{ max: 64, message: tr("适用版本不超过 64 个字符") }]}
          >
            <Input placeholder="3.2.0" />
          </Form.Item>
          <Form.Item label={tr("文件")} required>
            <Upload
              fileList={uploadFiles}
              maxCount={1}
              beforeUpload={() => false}
              onChange={({ fileList }) => setUploadFiles(fileList)}
            >
              <Button icon={<UploadOutlined />}>{tr("选择文件")}</Button>
            </Upload>
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        open={revisionTarget !== null}
        title={revisionTarget ? tr("追加修订：{{arg0}}", { arg0: revisionTarget.title }) : tr("追加修订")}
        confirmLoading={saving}
        onOk={() => void submitRevision()}
        onCancel={() => setRevisionTarget(null)}
        destroyOnClose
      >
        <Upload
          fileList={revisionFiles}
          maxCount={1}
          beforeUpload={() => false}
          onChange={({ fileList }) => setRevisionFiles(fileList)}
        >
          <Button icon={<UploadOutlined />}>{tr("选择文件")}</Button>
        </Upload>
        <Typography.Paragraph type="secondary" style={{ marginTop: 12, marginBottom: 0 }}>
          {tr("新修订会替换文档的当前文件，旧修订仍可下载追溯。")}</Typography.Paragraph>
      </Modal>

      <Modal
        open={editTarget !== null}
        title={tr("编辑文档")}
        confirmLoading={saving}
        onOk={() => void submitEdit()}
        onCancel={() => setEditTarget(null)}
      >
        <Form form={editForm} layout="vertical">
          <Form.Item name="title" label={tr("标题")} rules={[{ required: true, max: 256 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="visibility" label={tr("可见性")} rules={[{ required: true }]}>
            <Select
              options={[
                { value: 'internal', label: tr("仅内部") },
                { value: 'client', label: tr("客户端可见") },
              ]}
            />
          </Form.Item>
          <Form.Item
            name="applicableVersion"
            label={tr("适用版本（留空清除）")}
            rules={[{ max: 64, message: tr("适用版本不超过 64 个字符") }]}
          >
            <Input placeholder="3.2.0" />
          </Form.Item>
        </Form>
      </Modal>

      <Drawer
        open={detailId !== null}
        width={responsiveWidth(isMobile, 760)}
        title={detail.data ? detail.data.title : tr("文档详情")}
        onClose={() => setDetailId(null)}
        styles={{ body: { padding: isMobile ? 12 : 24 } }}
      >
        {detail.isError ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message={(detail.error as Error).message}
            action={
              <Button size="small" onClick={() => void detail.refetch()}>
                {tr("重试")}</Button>
            }
          />
        ) : null}
        {detail.data ? (
          <>
            <Descriptions
              size="small"
              column={{ xs: 1, md: 2 }}
              bordered
              items={[
                { key: 'collection', label: tr("集合"), children: detail.data.collectionKey },
                {
                  key: 'visibility',
                  label: tr("可见性"),
                  children: (
                    <Tag color={VISIBILITY_META[detail.data.visibility].color}>
                      {VISIBILITY_META[detail.data.visibility].label}
                    </Tag>
                  ),
                },
                {
                  key: 'applicableVersion',
                  label: tr("适用版本"),
                  children: detail.data.applicableVersion ?? '-',
                },
                { key: 'revision', label: tr("当前修订"), children: detail.data.revision },
                { key: 'fileName', label: tr("文件名"), children: detail.data.fileName },
                { key: 'size', label: tr("大小"), children: formatBytes(detail.data.sizeBytes) },
                {
                  key: 'sha',
                  label: 'SHA-256',
                  children: (
                    <Typography.Text code style={{ wordBreak: 'break-all' }}>
                      {detail.data.sha256}
                    </Typography.Text>
                  ),
                },
                {
                  key: 'updated',
                  label: tr("更新时间"),
                  children: formatDateTime(detail.data.updatedAt),
                },
              ]}
            />

            <Typography.Title level={5} style={{ marginTop: 24 }}>
              {tr("修订历史")}</Typography.Title>
            <Table<DocumentDetail['revisions'][number]>
              scroll={{ x: 'max-content' }}
              rowKey="id"
              size="small"
              dataSource={detail.data.revisions}
              columns={revisionColumns}
              pagination={responsivePagination(isMobile, { pageSize: 20 })}
            />
          </>
        ) : null}
      </Drawer>
    </Space>
  );
}
