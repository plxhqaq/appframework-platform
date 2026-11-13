import { Typography } from 'antd';
import type { ReactNode } from 'react';

/** 页面抬头：标题 + 一句用途说明。窄屏时说明文字换行，不跟标题挤一行。 */
export function PageHeading({
  title,
  description,
  extra,
}: {
  title: string;
  description?: ReactNode;
  extra?: ReactNode;
}) {
  return (
    <div className="page-heading">
      <div className="page-heading__text">
        <Typography.Title level={4} className="page-heading__title">
          {title}
        </Typography.Title>
        {description ? (
          <Typography.Text type="secondary" className="page-heading__desc">
            {description}
          </Typography.Text>
        ) : null}
      </div>
      {extra ? <div className="page-heading__extra">{extra}</div> : null}
    </div>
  );
}
