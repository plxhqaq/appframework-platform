#!/bin/sh
# 管理台容器启动时挑一份站点配置渲染出来，装进官方 nginx 镜像的入口脚本序列里
# （/docker-entrypoint.d/*.sh 按文件名顺序执行，20- 是镜像自带的 envsubst 模板）。
#
# 为什么在启动时挑、而不是构建时定死：开/关 https 于是只是重建容器，不用重新跑一遍
# pnpm 构建（两三分钟）；deploy.sh 的 --https / --no-https 才因此能当场生效。
# 证书同理：/etc/nginx/certs 是宿主机目录只读挂进来的，定时重签换掉文件后 nginx -s reload
# 就吃到新证书，全程不碰镜像。
set -eu

variant=http
if [ "${WEB_HTTPS:-0}" = "1" ]; then
  variant=https
fi

src="/etc/nginx/site.d/web.conf.${variant}"
[ -f "$src" ] || {
  echo "× 镜像里缺 ${src}（构建时 deploy/nginx/site.d 没拷进来？）" >&2
  exit 1
}

if [ "$variant" = https ]; then
  for f in web.crt web.key; do
    [ -f "/etc/nginx/certs/${f}" ] || {
      echo "× 站点按 https 渲染，但容器里看不到 /etc/nginx/certs/${f}。" >&2
      echo "  · 让脚本生成自签证书：bash deploy/deploy.sh up --https" >&2
      echo "  · 自己提供证书：把 web.crt 与 web.key 放进 .env 里的 WEB_CERT_DIR 那个目录，再重跑 up" >&2
      exit 1
    }
  done
fi

# 只替这两处：站点文件里 $host / $scheme / $http_upgrade 都是 nginx 自己的变量，
# 不列进替换名单就原样保留（列错了会把 nginx 配置渲染成空串，nginx -t 都过不去）。
envsubst '${API_UPSTREAM} ${WEB_PUBLIC_HTTPS_PORT}' < "$src" > /etc/nginx/conf.d/default.conf

echo "→ 管理台站点：${variant}，API upstream=${API_UPSTREAM}"
