#!/usr/bin/env bash
# 开发期「推倒重来」入口：等价于 bash deploy/deploy.sh uninstall，单独放一份好记的名字。
# 容器/网络/卷（嵌入库 pgdata + 附件 blobs）、本栈镜像、deploy/.env、自签证书与重签定时器
# 一并拆掉；外部库模式不会动别人的库。选项原样透传给 deploy.sh：
#   --yes          免交互确认（脚本里用）
#   --keep-images  保留镜像（下次 up 省一次构建）
#   --keep-env     保留 deploy/.env（口令与密钥沿用）
#   --keep-certs   保留自签证书
#   --purge-blobs  连 --blobs-dir 指向的宿主机附件目录一起删
#   --drop-external-db  外部库模式连数据一起清（DROP + 重建同名空库；默认不碰）
set -euo pipefail
exec bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/deploy.sh" uninstall "$@"
