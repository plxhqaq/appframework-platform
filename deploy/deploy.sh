#!/usr/bin/env bash
# 平台服务端一键起栈：检查依赖 → 生成 deploy/.env → 构建镜像 → 显式迁移 → 起服务 → 等就绪。
#
# 用法：
#   ./deploy/deploy.sh [动作] [选项]
#
# 动作：
#   up                            默认。首装生成随机口令与密钥：起库 → 迁移 → 构建 → 起 api
#                                 → 等 /ready → 起管理台（默认带自签 https）→ 等首页可开
#   up --use-existing-pg "<conn>" 复用现场已有的 PostgreSQL，不建数据库容器。conn 形如
#                                 Host=10.0.0.7;Port=5432;Database=platform;Username=platform;Password=***
#   migrate                       只跑数据库迁移（升级镜像后先迁移再放量）
#   fix-pg-password               嵌入库专用修复：把 pgdata 卷里角色的口令对到 .env 的
#                                 POSTGRES_PASSWORD 上。只 ALTER ROLE，不动数据、不改 .env；
#                                 撞了 28P01（删过 .env 或换过机器）用它，别删库
#   renew-cert                    自签证书剩余有效期不足 WEB_SSL_RENEW_DAYS 天时重签并 reload
#                                 管理台容器（--force 立刻重签）。root 下 up --https 会顺手装好
#                                 每天跑一次的 systemd 定时器，这个动作是它的即时/手工版
#   reset-admin-password          把 admin 的口令对回 .env 里的 SEED_ADMIN_PASSWORD。种子的口令
#                                 只在账号不存在时写入，所以「info 印的引导口令进不去」不会自己好；
#                                 .env 为空时先生成一个再写回。执行后该账号全部旧会话作废
#   status | logs [服务...] | health | down
#   uninstall                     开发期重置：拆掉本栈容器/网络/卷（嵌入库 pgdata 与附件 blobs）、
#                                 本栈镜像、deploy/.env、自签证书与重签定时器，回到「从没部署过」。
#                                 外部库模式不会 DROP 别人的库，只打印该就地执行的 SQL。
#                                 默认交互确认；--yes 免确认。想重新部署就紧接着 up
#   info                          只读重打现场要抄的信息（凭据脱敏），不碰 docker、不改任何文件
#   info --show-secrets           同上，并打印 JWT 签名密钥、数据库口令、授权私钥全文
#
# 选项（现场要改的东西都做成了选项；每一项都会写回 deploy/.env，下次重跑沿用，不用重复带）：
#   --use-existing-pg "<conn>"  写进 .env 后长期生效，下次直接 ./deploy.sh up
#   --api-port <n>              API 发布到宿主机的端口，默认 8080。只挪对外端口，
#                               容器内仍是 8080（改它不影响客户端配的服务地址以外的任何东西）
#   --api-bind <地址>           对外绑定地址：0.0.0.0（默认，客户端直连）| 127.0.0.1（前面有反代）
#   --pg-port <n>               嵌入库发布到宿主机 127.0.0.1 的运维直连端口，默认 5433
#   --blobs-dir <绝对路径>      附件与安装包改存宿主机目录（默认存具名卷 blobs，在数据盘上时用）
#   --web | --no-web            起不起管理台容器（--no-web：只做接口对接的机器、或坚持挂在宿主 nginx 上）
#   --web-port <n>              管理台 http 发布端口，默认 8081；开 https 时它是 301 跳板与设备通路
#   --web-https-port <n>        管理台 https 发布端口，默认 8443（浏览器实际访问的端口）
#   --web-bind <地址>           管理台对外绑定地址，默认 0.0.0.0；127.0.0.1 = 只留给宿主反代
#   --https | --no-https        管理台要不要 TLS。默认开：自签证书，口令不裸跑，Secure Cookie 才生效
#   --cert-dir <绝对路径>       证书目录，默认 /var/lib/appframework-platform/certs
#                               （写不进去时自动退回 <仓库>/deploy/certs 并说明）
#   --server-name <名字>        自签证书的主机名（CN 与 SAN 里的 DNS 项），默认取 hostname -f
#   --days <n>                  自签证书剩多少天内才重签，默认 30
#   --no-cert-timer             不装每天定时重签的 systemd 定时器（自己安排续期时用）
#   --force                     只给 renew-cert：不管还剩多少天，立刻重签
#   --yes                       只给 uninstall：跳过交互确认（脚本/CI 里用）
#   --keep-images               只给 uninstall：保留本栈构建的镜像，下次 up 省一次构建
#   --keep-env                  只给 uninstall：保留 deploy/.env（口令与密钥沿用）
#   --keep-certs                只给 uninstall：保留自签证书（浏览器已记住的例外继续有效）
#   --purge-blobs               只给 uninstall：连 --blobs-dir 指向的宿主机附件目录一起删；
#                               默认只删具名卷 blobs，绑到数据盘的目录不会被脚本擅自清空
#   --drop-external-db          只给 uninstall：外部库模式下连数据一起清（DROP + 重建同名空库）。
#                               不加就只打印该执行的 SQL——外部库可能还装着别的应用的库
#   --no-license-key            不生成授权签发私钥，授权模块整体保持 unmanaged
#   --skip-preflight            跳过端口占用预探测（明知端口能用时；谨慎，撞端口要等构建完才报）
#   --show-secrets              汇总里打印密钥全文（默认脱敏）
#   -h | --help                 帮助
#
# 端口这类值不建议手工改 compose 文件：改 .env 或用上面的选项，几份 yml 保持与仓库一致，
# 升级时才能直接覆盖文件而不出冲突。
#
# 管理台为什么单独占端口（现场机器上通常已经有一个 nginx 在代理别的应用）：本栈不去动宿主
# nginx，也不在 443 上再挂一个 default_server——那会把别人的站点顶掉。默认发布 8081/8443
# 就是一套并排跑、随时可撤的独立站点；要把管理台并进宿主 nginx 的 80/443，照抄
# deploy/nginx/site.d/*.http 里那几个 location 即可（README 有说明）。
#
# 刻意保留的三条约定：
#   · 不覆盖 .env 里已有的值——口令只生成一次；但**留空**的密钥会就地补写并告知，因为服务端
#     自己随机生成的那份只在首启打一次日志，容器一重启就没人查得回来。
#   · 授权私钥只要非空就绝不重新生成：换钥会让已签发的授权快照与离线码全部失效。
#   · 密钥全文默认不进 stdout（JWT 签名密钥泄露 = 任何人可伪造令牌），要全文用 --show-secrets。
#   第四条是自签证书：能复用就复用，每次部署都重签会让所有浏览器刚记住的「继续前往」例外作废。
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_FILE="$ROOT_DIR/deploy/docker-compose.yml"
PG_COMPOSE_FILE="$ROOT_DIR/deploy/docker-compose.postgres.yml"
WEB_COMPOSE_FILE="$ROOT_DIR/deploy/docker-compose.web.yml"
WEB_HTTPS_COMPOSE_FILE="$ROOT_DIR/deploy/docker-compose.web.https.yml"
ENV_FILE="$ROOT_DIR/deploy/.env"
ENV_EXAMPLE="$ROOT_DIR/deploy/env.example"
SYSTEMD_DIR="$ROOT_DIR/deploy/systemd"
# 打给现场看的每一条命令都用这个绝对路径，不写死 ./deploy/deploy.sh：现场经常在 deploy/ 目录里
# 直接 ./deploy.sh，也可能在仓库根跑，写死的相对路径总有一种会跑出「No such file or directory」。
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

log() { printf '%s\n' "$*"; }
warn() { printf '! %s\n' "$*" >&2; }
die() { printf '× %s\n' "$*" >&2; exit 1; }

usage() {
  awk 'NR>1 && /^set -euo/{exit} NR>1{sub(/^# ?/,""); print}' "${BASH_SOURCE[0]}"
}

# ── 参数 ────────────────────────────────────────────────────────────────
ACTION="up"
case "${1:-}" in
  up|migrate|status|logs|health|down|info|fix-pg-password|renew-cert|reset-admin-password|uninstall) ACTION="$1"; shift ;;
esac

# 选项校验放在参数循环之前定义：bash 按定义顺序执行函数，循环里要用到它们。
need_value() {  # need_value <选项名> <参数值>
  [ -n "${2:-}" ] || die "$1 需要一个参数值"
  case "${2:-}" in --*) die "$1 需要一个参数值（紧跟的 $2 看着像下一个选项）" ;; esac
}

check_port() {  # check_port <选项名> <端口>
  case "$2" in
    ''|*[!0-9]*) die "$1 需要一个数字端口（得到：$2）" ;;
  esac
  if [ "$2" -lt 1 ] || [ "$2" -gt 65535 ]; then die "$1 端口越界（1–65535）：$2"; fi
}

check_bind_addr() {  # check_bind_addr <选项名> <地址>；只挡空格与引号这类会撑坏 compose 的输入
  case "$2" in
    *[[:space:]]*|*'"'*|*"'"*|*'$'*) die "$1 不是合法的绑定地址：$2" ;;
  esac
}

check_abs_path() {  # check_abs_path <选项名> <路径>；相对路径会被 compose 按 yml 所在目录解释
  case "$2" in
    /*) ;;
    *) die "$1 需要绝对路径：$2（相对路径会被 compose 当成 $ROOT_DIR/deploy/ 下的目录，指到跑偏的位置）" ;;
  esac
}

check_days() {  # check_days <选项名> <天数>；上界挡掉「阈值大过有效期」这种把重签变成常签的写法
  case "$2" in
    ''|*[!0-9]*) die "$1 需要一个整数天数（得到：$2）" ;;
  esac
  if [ "$2" -lt 1 ] || [ "$2" -gt 365 ]; then
    die "$1 需要在 1–365 之间：$2（自签证书一共才 397 天，阈值再大就变成每次部署都重签，所有浏览器的「继续前往」例外跟着作废）"
  fi
}

SHOW_SECRETS=0
NO_LICENSE_KEY=0
SKIP_PREFLIGHT=0
NO_CERT_TIMER=0
FORCE_SIGN=0
UNINSTALL_YES=0
UNINSTALL_KEEP_IMAGES=0
UNINSTALL_KEEP_ENV=0
UNINSTALL_KEEP_CERTS=0
UNINSTALL_PURGE_BLOBS=0
UNINSTALL_DROP_EXTERNAL_DB=0
UNINSTALL_HAS_ENV=0
EXISTING_PG=""
OPT_API_PORT=""
OPT_API_BIND=""
OPT_PG_PORT=""
OPT_BLOBS_DIR=""
OPT_WEB_HTTP_PORT=""
OPT_WEB_HTTPS_PORT=""
OPT_WEB_BIND=""
OPT_WEB=""
OPT_CERT_DIR=""
OPT_SERVER_NAME=""
OPT_SSL_DAYS=""
OPT_WEB_HTTPS=""
SERVICES=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --show-secrets)   SHOW_SECRETS=1 ;;
    --no-license-key) NO_LICENSE_KEY=1 ;;
    --skip-preflight) SKIP_PREFLIGHT=1 ;;
    --web)            OPT_WEB=1 ;;
    --no-web)         OPT_WEB=0 ;;
    --no-cert-timer)  NO_CERT_TIMER=1 ;;
    --https)          OPT_WEB_HTTPS=1 ;;
    --no-https)       OPT_WEB_HTTPS=0 ;;
    --force)          FORCE_SIGN=1 ;;
    --yes|-y)         UNINSTALL_YES=1 ;;
    --keep-images)    UNINSTALL_KEEP_IMAGES=1 ;;
    --keep-env)       UNINSTALL_KEEP_ENV=1 ;;
    --keep-certs)     UNINSTALL_KEEP_CERTS=1 ;;
    --purge-blobs)    UNINSTALL_PURGE_BLOBS=1 ;;
    --drop-external-db) UNINSTALL_DROP_EXTERNAL_DB=1 ;;
    --use-existing-pg)
      need_value "$1" "${2:-}"; EXISTING_PG="$2"; shift ;;
    --api-port)   need_value "$1" "${2:-}"; OPT_API_PORT="$2"; check_port "$1" "$2"; shift ;;
    --api-bind)   need_value "$1" "${2:-}"; OPT_API_BIND="$2"; check_bind_addr "$1" "$2"; shift ;;
    --pg-port)    need_value "$1" "${2:-}"; OPT_PG_PORT="$2"; check_port "$1" "$2"; shift ;;
    --blobs-dir)  need_value "$1" "${2:-}"; OPT_BLOBS_DIR="$2"; check_abs_path "$1" "$2"; shift ;;
    --web-port)       need_value "$1" "${2:-}"; OPT_WEB_HTTP_PORT="$2"; check_port "$1" "$2"; shift ;;
    --web-https-port) need_value "$1" "${2:-}"; OPT_WEB_HTTPS_PORT="$2"; check_port "$1" "$2"; shift ;;
    --web-bind)       need_value "$1" "${2:-}"; OPT_WEB_BIND="$2"; check_bind_addr "$1" "$2"; shift ;;
    --cert-dir)       need_value "$1" "${2:-}"; OPT_CERT_DIR="$2"; check_abs_path "$1" "$2"; shift ;;
    --server-name)    need_value "$1" "${2:-}"; OPT_SERVER_NAME="$2"; shift ;;
    --days)           need_value "$1" "${2:-}"; OPT_SSL_DAYS="$2"; check_days "$1" "$2"; shift ;;
    -h|--help)    usage; exit 0 ;;
    --*)          die "未知选项：$1（-h 看用法）" ;;
    *)            SERVICES+=("$1") ;;
  esac
  shift
done

# 证书主机名会进 openssl 配置与 SAN 串：只认主机名字符（字母数字、点、连字符、下划线），
# 别把换行、方括号、逗号这类能把配置文件顶歪的输入放进来。
case "$OPT_SERVER_NAME" in
  *[^A-Za-z0-9._-]*) die "--server-name 只能用字母、数字、点、连字符：$OPT_SERVER_NAME" ;;
esac

if [ -n "$EXISTING_PG$OPT_API_PORT$OPT_API_BIND$OPT_PG_PORT$OPT_BLOBS_DIR$OPT_WEB_HTTP_PORT$OPT_WEB_HTTPS_PORT$OPT_WEB_BIND$OPT_WEB$OPT_CERT_DIR$OPT_SERVER_NAME$OPT_SSL_DAYS$OPT_WEB_HTTPS" ] \
   && [ "$ACTION" != up ] && [ "$ACTION" != migrate ] && [ "$ACTION" != renew-cert ]; then
  die "这些选项只在 up/migrate/renew-cert 时写 .env（其它动作请直接改 $ENV_FILE 里的对应键）：
  --use-existing-pg --api-port --api-bind --pg-port --blobs-dir
  --web/--no-web --web-port --web-https-port --web-bind --cert-dir --server-name --days --https/--no-https"
fi

# --force 只对 renew-cert 有意义：留在 up 里会被读成「这次部署强制重签一张新证书」，
# 而那正是每次部署都换证书、把所有浏览器例外作废的坏味道。--days 同理（它写的是阈值）。
if [ "$FORCE_SIGN" = 1 ] && [ "$ACTION" != renew-cert ]; then
  die "--force 只给 renew-cert 用（up 会按剩余有效期自己决定要不要重签，不需要顶）"
fi

# 重置选项只属于 uninstall：写在 up 里会被读成「这次部署顺手删掉点什么」，代价太大。
if [ "$UNINSTALL_YES$UNINSTALL_KEEP_IMAGES$UNINSTALL_KEEP_ENV$UNINSTALL_KEEP_CERTS$UNINSTALL_PURGE_BLOBS$UNINSTALL_DROP_EXTERNAL_DB" != "000000" ] \
   && [ "$ACTION" != uninstall ]; then
  die "--yes / --keep-images / --keep-env / --keep-certs / --purge-blobs / --drop-external-db 只给 uninstall 用"
fi

# 只有 logs 收位置参数（服务名）。其它动作收到多余参数基本是动作本身拼错了，
# 静默忽略的话 `deploy.sh ap` 会被当成 up 真去构建部署，现场完全看不出打错了字。
if [ "${#SERVICES[@]}" -gt 0 ] && [ "$ACTION" != logs ]; then
  die "$ACTION 不接收这些参数：${SERVICES[*]}（动作拼错了？可用 up|migrate|fix-pg-password|renew-cert|reset-admin-password|status|logs|health|down|uninstall|info）"
fi

# ── 依赖检查（info 是只读动作，不需要 docker） ──────────────────────────
# 探测 compose 命令并填好 COMPOSE，成功返回 0、失败返回 1，一个字都不打印：
# renew-cert 由 systemd 定时器跑，那条路径上 docker 可能在，也可能只是守护进程还没起来，
# 它需要的是「不行就退回手工命令」，而不是把一次已经成功的续期整体报错。
resolve_compose_bin() {
  command -v docker >/dev/null 2>&1 || return 1
  if docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
  elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE=(docker-compose)
  else
    return 1
  fi
  return 0
}

require_docker() {
  command -v docker >/dev/null 2>&1 || die "没找到 docker 命令。"
  resolve_compose_bin || die "需要 docker compose（v2）或 docker-compose。"
  docker info >/dev/null 2>&1 || die "docker 守护进程没起来（systemctl start docker）。"
}

# 模式开关决定附加哪几份文件。所有 compose 调用都走 compose()，保证 up / down / status /
# logs 看到同一套服务——否则从嵌入库切到外部库、或关掉管理台以后，down 停不掉先前的容器。
COMPOSE_FILES=()
setup_compose() {
  COMPOSE_FILES=(-f "$COMPOSE_FILE")
  if [ "$EMBEDDED_PG" = 1 ]; then COMPOSE_FILES+=(-f "$PG_COMPOSE_FILE"); fi
  if [ "$WEB_ENABLED_ON" = 1 ]; then
    COMPOSE_FILES+=(-f "$WEB_COMPOSE_FILE")
    if [ "$WEB_HTTPS_ON" = 1 ]; then COMPOSE_FILES+=(-f "$WEB_HTTPS_COMPOSE_FILE"); fi
  fi
}

# 打给现场看的 docker compose 命令里的那串 -f：跟 setup_compose 用同一个来源，
# 免得手工写死两份文件、现场照着敲却发现管理台容器没被停掉。
compose_files_display() {
  local f out=""
  for f in "${COMPOSE_FILES[@]}"; do out="$out -f \"$f\""; done
  printf '%s' "$out"
}
compose() { "${COMPOSE[@]}" "${COMPOSE_FILES[@]}" --env-file "$ENV_FILE" "$@"; }

# ── .env 读写 ───────────────────────────────────────────────────────────
# 读一个键：只去首尾空白与成对双引号（口令里的空格合法，不能整串删空白）。
env_value() {
  local v
  v="$(grep -E "^$1=" "$ENV_FILE" 2>/dev/null | tail -1 | cut -d= -f2- || true)"
  v="${v#"${v%%[![:space:]]*}"}"
  v="${v%"${v##*[![:space:]]}"}"
  case "$v" in
    '"'*) [ "${v: -1}" = '"' ] && v="${v:1:${#v}-2}" ;;
  esac
  printf '%s' "$v"
}

# 写一个键：只动这一行，且不经 sed（值里有 / + = # & 都不用转义）
env_set() {
  local key="$1" val="$2" line tmp found=0
  tmp="$(mktemp)"
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
      "$key="*) printf '%s=%s\n' "$key" "$val" >> "$tmp"; found=1 ;;
      *)        printf '%s\n' "$line" >> "$tmp" ;;
    esac
  done < "$ENV_FILE"
  [ "$found" = 1 ] || printf '%s=%s\n' "$key" "$val" >> "$tmp"
  mv "$tmp" "$ENV_FILE"
  chmod 600 "$ENV_FILE" 2>/dev/null || true
}

# 老 .env 没这个键时补一行默认值，后面的分支判断才不会读到空串
env_ensure_key() {
  grep -qE "^$1=" "$ENV_FILE" 2>/dev/null || printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
}

# 仓库里 env.example 是 CRLF（Windows 检出，或 zip 传到 Linux），compose 会把行尾的 \r
# 当成值的一部分：口令多个 \r 就是「密码不对」，库名多个 \r 就是「库不存在」。
env_normalize() {
  grep -qU $'\r' "$ENV_FILE" 2>/dev/null || return 0
  local tmp
  tmp="$(mktemp)"
  tr -d '\r' < "$ENV_FILE" > "$tmp" && mv "$tmp" "$ENV_FILE"
  chmod 600 "$ENV_FILE" 2>/dev/null || true
}

# 一个布尔开关键的当前取值，打印 1/0。留空（老 .env 里根本没写过这个键）按默认值算，
# 这样 ensure_env 会补键的路径与 info/status 只读 .env 的路径看到的开关一定一致——
# 两条路径读出不一样，就变成「info 说没开 https，up 却起了 https 站点」。
env_flag() {  # env_flag <键> <缺省：1 表示默认开>
  local v second="${2:-1}"
  v="$(env_value "$1")"
  case "$v" in
    true|TRUE|True|1|yes|YES|on|ON) printf '1'; return 0 ;;
    false|FALSE|False|0|no|NO|off|OFF) printf '0'; return 0 ;;
  esac
  [ "$v" = "" ] || warn "$ENV_FILE 里 $1 的值看不懂（$v），按默认算。"
  [ "$second" = 1 ] && printf '1' || printf '0'
}

rand_hex() {  # rand_hex <字节数>
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex "$1"
  else
    od -An -tx1 -N "$1" /dev/urandom | tr -d ' \n'
  fi
}

# ── 库模式与本次运行用到的变量 ──────────────────────────────────────────
EMBEDDED_PG=1
WEB_ENABLED_ON=1
WEB_HTTPS_ON=1
# 证书目录默认放在仓库之外：这份仓库经常整目录 zip 到测试机，私钥跟着走一遍就多看一处泄露面。
DEFAULT_CERT_DIR="/var/lib/appframework-platform/certs"
CERT_TIMER_UNIT="appframework-platform-cert-renew.timer"
CERT_SERVICE_UNIT="appframework-platform-cert-renew.service"
# 这三个是「本次运行对证书做了什么」的口径，只给 info/汇总读：没走 https 路径时保持空，
# print_info 要能分清「纯 http」和「https 但这次没查过证书」两种情况，靠 set -u 报错来提醒
# 自己忘了初始化是最坏的一种提醒方式。
WEB_SSL_KIND=""
WEB_SSL_SELF=0
WEB_CERT_RENEWED=0
CERT_TIMER_STATE=""

load_mode() {
  case "$(env_value USE_EMBEDDED_POSTGRES)" in
    false|FALSE|False|0|no|off) EMBEDDED_PG=0 ;;
    *)                          EMBEDDED_PG=1 ;;
  esac
}

# 管理台两层开关：WEB_ENABLED 决定起不起那个容器，WEB_HTTPS 决定站点用 http 还是 https 模板。
# 读的是 .env 的值（容器里那两个环境变量由叠加的 yml 写死，切换靠挂哪份文件）。
load_web_mode() {
  WEB_ENABLED_ON="$(env_flag WEB_ENABLED 1)"
  WEB_HTTPS_ON="$(env_flag WEB_HTTPS 1)"
}

load_vars() {
  load_mode
  load_web_mode
  setup_compose
  API_PORT="$(env_value API_PORT)";                     API_PORT="${API_PORT:-8080}"
  API_BIND_HOST="$(env_value API_BIND_HOST)";           API_BIND_HOST="${API_BIND_HOST:-0.0.0.0}"
  BLOB_PATH="$(env_value BLOB_STORAGE_HOST_PATH)"
  PG_HOST="$(env_value POSTGRES_HOST)";                PG_HOST="${PG_HOST:-postgres}"
  PG_PORT="$(env_value POSTGRES_PORT)";                PG_PORT="${PG_PORT:-5432}"
  PG_DB="$(env_value POSTGRES_DB)";                    PG_DB="${PG_DB:-appframework_platform}"
  PG_USER="$(env_value POSTGRES_USER)";                PG_USER="${PG_USER:-platform}"
  PG_PUBLISH_PORT="$(env_value POSTGRES_PUBLISH_PORT)"; PG_PUBLISH_PORT="${PG_PUBLISH_PORT:-5433}"
  WEB_HTTP_PORT="$(env_value WEB_HTTP_PORT)";           WEB_HTTP_PORT="${WEB_HTTP_PORT:-8081}"
  WEB_HTTPS_PORT="$(env_value WEB_HTTPS_PORT)";         WEB_HTTPS_PORT="${WEB_HTTPS_PORT:-8443}"
  WEB_BIND_HOST="$(env_value WEB_BIND_HOST)";           WEB_BIND_HOST="${WEB_BIND_HOST:-0.0.0.0}"
  WEB_CERT_DIR="$(env_value WEB_CERT_DIR)";             WEB_CERT_DIR="${WEB_CERT_DIR:-$DEFAULT_CERT_DIR}"
  WEB_SSL_RENEW_DAYS="$(env_value WEB_SSL_RENEW_DAYS)"; WEB_SSL_RENEW_DAYS="${WEB_SSL_RENEW_DAYS:-30}"
  WEB_SERVER_NAME="$(env_value WEB_SERVER_NAME)"
  WEB_API_UPSTREAM="$(env_value WEB_API_UPSTREAM)";     WEB_API_UPSTREAM="${WEB_API_UPSTREAM:-api:8080}"
  # 浏览器实际访问的 https 端口：默认就是发布端口，另有前置反代接管 443 时才手工设
  WEB_PUBLIC_HTTPS_PORT="$(env_value WEB_PUBLIC_HTTPS_PORT)"; WEB_PUBLIC_HTTPS_PORT="${WEB_PUBLIC_HTTPS_PORT:-$WEB_HTTPS_PORT}"
  QR_APPROVAL_URL_BASE="$(env_value QR_APPROVAL_URL_BASE)"
  base="http://localhost:$API_PORT"
  web_http_base="http://localhost:$WEB_HTTP_PORT"
  web_https_base="https://localhost:$WEB_HTTPS_PORT"
}

# ── 授权签发密钥（首装自动生成；私钥非空绝不重生成） ────────────────────
# 服务端只用私钥签发；公钥由私钥派生后写进 .env 备查，并通过授权状态接口对外可读。
# 客户端认的是公钥指纹（keyId = "lk-" + SHA-256(公钥 SPKI) 前 16 位），两端对不上时客户端
# 一律按 unmanaged 处理——不锁机，但授权形同没启用。所以分支口径是：
#   · 私钥非空 → 绝不重生成；只把缺失的公钥记录补写回 .env（手工配私钥时自愈）
#   · 私钥公钥都空 → 生成一对（--no-license-key 或 .env 里 LICENSING_GENERATE_KEY=false 跳过）
#   · 没有 openssl → 只告警，保持 unmanaged，不阻断部署
LICENSE_STATE=""

license_pub_from_priv() {  # 成功时打印公钥 SPKI Base64
  local priv="$1" tmp pub="" ok=0
  command -v openssl >/dev/null 2>&1 || return 1
  command -v base64 >/dev/null 2>&1 || return 1
  tmp="$(mktemp -d)"
  if printf '%s' "$priv" | base64 -d > "$tmp/priv.der" 2>/dev/null \
     && openssl pkey -inform DER -in "$tmp/priv.der" -pubout -outform DER -out "$tmp/pub.der" 2>/dev/null; then
    pub="$(openssl base64 -A -in "$tmp/pub.der")"
    ok=1
  fi
  rm -rf "$tmp"
  [ "$ok" = 1 ] || return 1
  printf '%s' "$pub"
}

license_fingerprint() {  # 与 LicenseCryptography.ComputeFingerprint 同口径：SPKI 字节的 SHA-256 小写十六进制
  printf '%s' "$1" | base64 -d 2>/dev/null | openssl dgst -sha256 -r 2>/dev/null | cut -d' ' -f1
}

ensure_license_key() {
  local priv pub tmp pem priv_der pub_der ok=0
  priv="$(env_value LICENSING_SIGNING_KEY_PKCS8)"
  pub="$(env_value LICENSING_PUBLIC_KEY_SPKI_BASE64)"

  if [ -n "$priv" ]; then
    LICENSE_STATE="沿用既有私钥"
    if [ -z "$pub" ]; then
      if pub="$(license_pub_from_priv "$priv")"; then
        env_set LICENSING_PUBLIC_KEY_SPKI_BASE64 "$pub"
        LICENSE_STATE="沿用既有私钥（本次补写公钥备查）"
        log "授权私钥已有、公钥记录为空 → 已由私钥派生并写回 .env。"
      else
        warn "授权私钥不是合法的 ECDSA P-256 PKCS#8：保持原样，请人工修复（当前按 unmanaged 运行）。"
        LICENSE_STATE="异常（私钥无法派生公钥）"
      fi
    fi
    return 0
  fi

  if [ "$NO_LICENSE_KEY" = 1 ] || [ "$(env_value LICENSING_GENERATE_KEY)" = "false" ]; then
    LICENSE_STATE="未生成（按要求保持 unmanaged）"
    return 0
  fi
  if ! command -v openssl >/dev/null 2>&1; then
    warn "未找到 openssl：无法生成授权签发密钥，服务端按 unmanaged 运行（客户端不受限，不阻断部署）。"
    warn "  要启用授权：装好 openssl 重跑本脚本，或手工填 LICENSING_SIGNING_KEY_PKCS8。"
    LICENSE_STATE="未生成（缺 openssl）"
    return 0
  fi

  tmp="$(mktemp -d)"
  pem="$tmp/key.pem"; priv_der="$tmp/priv.der"; pub_der="$tmp/pub.der"
  if openssl ecparam -name prime256v1 -genkey -noout -out "$pem" 2>/dev/null \
     && openssl pkcs8 -topk8 -nocrypt -in "$pem" -outform DER -out "$priv_der" 2>/dev/null \
     && openssl ec -in "$pem" -pubout -outform DER -out "$pub_der" 2>/dev/null; then
    priv="$(openssl base64 -A -in "$priv_der")"
    pub="$(openssl base64 -A -in "$pub_der")"
    ok=1
  fi
  rm -rf "$tmp"
  if [ "$ok" != 1 ]; then
    warn "授权密钥生成失败（openssl 版本过旧？）：服务端按 unmanaged 运行，客户端不受限。"
    LICENSE_STATE="未生成（生成失败）"
    return 0
  fi
  env_set LICENSING_SIGNING_KEY_PKCS8 "$priv"
  env_set LICENSING_PUBLIC_KEY_SPKI_BASE64 "$pub"
  LICENSE_STATE="本次新生成"
}

# ── .env 生成 / 补空 ────────────────────────────────────────────────────
# 值全部用十六进制 + 固定前后缀：不含 # / 空格 / 引号，compose 解析不会踩坑；
# 管理员口令满足策略（8–128 且同时含字母与数字）。
ENV_IS_NEW=0
ensure_env() {
  if [ -f "$ENV_FILE" ]; then
    env_normalize
    log "使用已有 $ENV_FILE（不覆盖已有值，只补写留空的密钥）。"
  else
    [ -f "$ENV_EXAMPLE" ] || die "缺少 $ENV_EXAMPLE。"
    tr -d '\r' < "$ENV_EXAMPLE" > "$ENV_FILE"
    chmod 600 "$ENV_FILE" 2>/dev/null || true
    ENV_IS_NEW=1
  fi

  env_ensure_key USE_EMBEDDED_POSTGRES true
  env_ensure_key POSTGRES_HOST postgres
  env_ensure_key POSTGRES_PORT 5432
  env_ensure_key POSTGRES_PUBLISH_PORT 5433
  env_ensure_key API_PORT 8080
  env_ensure_key API_BIND_HOST 0.0.0.0
  env_ensure_key BLOB_STORAGE_HOST_PATH ""
  env_ensure_key LICENSING_GENERATE_KEY true
  env_ensure_key LICENSING_PUBLIC_KEY_SPKI_BASE64 ""
  env_ensure_key WEB_ENABLED true
  env_ensure_key WEB_HTTPS true
  env_ensure_key WEB_HTTP_PORT 8081
  env_ensure_key WEB_HTTPS_PORT 8443
  env_ensure_key WEB_BIND_HOST 0.0.0.0
  env_ensure_key WEB_CERT_DIR "$DEFAULT_CERT_DIR"
  env_ensure_key WEB_SSL_RENEW_DAYS 30
  env_ensure_key WEB_SERVER_NAME ""
  env_ensure_key WEB_API_UPSTREAM api:8080
  env_ensure_key WEB_PUBLIC_HTTPS_PORT ""
  env_ensure_key QR_APPROVAL_URL_BASE ""
  env_ensure_key SINGLE_LOGIN_MAX_MINUTES 120
  env_ensure_key QR_LOGIN_MAX_MINUTES 120

  # 现场参数选项 → .env（顺序在切库模式之前，两者都只是写键，不涉及口令生成）
  apply_site_opts
  # 先切外部库模式，再决定要不要随机生成数据库口令：反过来的话生成的那份会被连接串盖掉
  [ -n "$EXISTING_PG" ] && apply_existing_pg "$EXISTING_PG"
  load_mode

  if [ "$ENV_IS_NEW" = 1 ]; then
    if [ "$EMBEDDED_PG" = 1 ]; then
      env_set POSTGRES_PASSWORD "$(rand_hex 24)"
    fi
    env_set SEED_ADMIN_PASSWORD "Adm-$(rand_hex 12)-7"
  fi

  # 这两项在 yml 里是必填，留空时的报错只有一句「必须设置」。与其让人对着那句话猜，
  # 不如就地生成、写回，并把值告诉现场。
  if [ -z "$(env_value JWT_SIGNING_KEY)" ]; then
    env_set JWT_SIGNING_KEY "$(rand_hex 32)"
    [ "$ENV_IS_NEW" = 1 ] || log "JWT_SIGNING_KEY 原为留空 → 本次新生成并写回（此后重跑不再变；换它会让已发令牌全部失效）。"
  fi
  if [ "$EMBEDDED_PG" != 1 ] && [ -z "$(env_value POSTGRES_PASSWORD)" ]; then
    die "外部库模式下 POSTGRES_PASSWORD 必须填既有库的账号口令（这种口令不能随机生成），或用 bash $SELF up --use-existing-pg \"Host=...;Password=***\""
  fi

  ensure_license_key
  load_vars
  ensure_qr_approval_base
}

# 二维码里写的是手机相机直接打开的审批页链接，基址必须显式喂给 API：
# nginx 转发的 Host 不带端口，API 从请求里推出来的地址手机扫开连不上。
# 留空时按「证书 CN 或本机内网 IP + 浏览器实际访问的端口」推一个写回 .env；
# 已经有值就一个字都不动——现场可能填的是外部域名，脚本猜不出来。
ensure_qr_approval_base() {
  [ -n "$(env_value QR_APPROVAL_URL_BASE)" ] && return 0
  [ "$(env_flag WEB_ENABLED 1)" = 1 ] || return 0

  local host scheme port
  host="$(env_value WEB_SERVER_NAME)"
  host="${host:-$(lan_ip)}"
  if [ "$(env_flag WEB_HTTPS 1)" = 1 ]; then
    scheme=https
    port="$(env_value WEB_PUBLIC_HTTPS_PORT)"
    port="${port:-$WEB_HTTPS_PORT}"
  else
    scheme=http
    port="$WEB_HTTP_PORT"
  fi

  QR_APPROVAL_URL_BASE="$scheme://$host:$port"
  env_set QR_APPROVAL_URL_BASE "$QR_APPROVAL_URL_BASE"
  log "扫码审批基址 → QR_APPROVAL_URL_BASE=$QR_APPROVAL_URL_BASE（写回 .env）。"
  log "  二维码里就是这个地址，手机必须能访问到它；换网段或上域名后改这一项再 bash $SELF up。"
}

# 从连接串取一个参数（只认 Host=..;Port=.. 这种值里不含分号的常见写法）
conn_param() {
  local key="$1" cs="$2"
  printf '%s' "$cs" | tr ';' '\n' \
    | grep -iE "^[[:space:]]*${key}=" | tail -1 | cut -d= -f2- \
    | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e 's/^"//' -e 's/"$//' || true
}

# 复用现场已有的 PG：把连接串拆进 .env，并关掉建库容器
apply_existing_pg() {
  local conn="$1" host port db user pass
  host="$(conn_param Host "$conn")"
  port="$(conn_param Port "$conn")";     port="${port:-5432}"
  db="$(conn_param Database "$conn")"
  user="$(conn_param Username "$conn")"
  pass="$(conn_param Password "$conn")"
  [ -n "$db" ]   || die "连接串里没找到 Database=：$conn"
  [ -n "$user" ] || die "连接串里没找到 Username=：$conn"
  [ -n "$pass" ] || die "连接串里没找到 Password=：$conn"

  # 容器里的 127.0.0.1 是容器自己。连宿主机自装的 PG 必须走 host-gateway 别名，
  # 不改写的话现场只会看到迁移容器一句 connection refused。
  case "$host" in
    127.0.0.1|localhost|::1|"")
      host="host.docker.internal"
      log "连接串指向宿主机 → POSTGRES_HOST 改用 host.docker.internal（容器内 127.0.0.1 是容器自己）。" ;;
  esac
  case "$pass" in
    *'$'*) warn "数据库口令里有 \$：compose 会当变量插值，请在 .env 里写成 \$\$，否则迁移会报口令错误。" ;;
  esac

  env_set USE_EMBEDDED_POSTGRES false
  env_set POSTGRES_HOST "$host"
  env_set POSTGRES_PORT "$port"
  env_set POSTGRES_DB "$db"
  env_set POSTGRES_USER "$user"
  env_set POSTGRES_PASSWORD "$pass"
  log "已把 $ENV_FILE 切到外部库：$host:$port/$db（用户 $user），不再创建数据库容器。"
  EXISTING_PG=""
}

# ── 现场参数（端口 / 绑定地址 / 附件目录）───────────────────────────────
# 一律写进 .env，不做「只对本次命令生效」的临时覆盖：这类值必须与首装保持一致，否则重跑
# up 时 compose 会另起一个新容器而旧容器还在跑，现场看到的是「部署成功但接口没变」，
# 比直接报错更难查。改完 .env 里长期生效，下次直接 ./deploy.sh up 即可。
apply_site_opt() {  # apply_site_opt <env键> <选项值> <选项名> <一句话说明>
  local key="$1" val="$2" opt="$3" note="$4" cur
  [ -n "$val" ] || return 0
  cur="$(env_value "$key")"
  if [ -z "$cur" ]; then
    log "$opt：$key=$val（$note）"
  elif [ "$cur" != "$val" ]; then
    log "$opt：$key 由 $cur 改为 $val（写回 .env，下次重跑沿用；$note）。"
  fi
  env_set "$key" "$val"
}

apply_site_opts() {
  apply_site_opt API_PORT               "$OPT_API_PORT"  "--api-port"  "只挪宿主机对外端口，容器内仍是 8080"
  apply_site_opt API_BIND_HOST         "$OPT_API_BIND"  "--api-bind"  "0.0.0.0=客户端可直连，127.0.0.1=只交给本机反代"
  apply_site_opt POSTGRES_PUBLISH_PORT "$OPT_PG_PORT"   "--pg-port"   "嵌入库发布到宿主机 127.0.0.1 的运维直连端口，API 不看它"
  apply_site_opt BLOB_STORAGE_HOST_PATH "$OPT_BLOBS_DIR" "--blobs-dir" "附件与安装包存到这个宿主机目录"
  apply_site_opt WEB_HTTP_PORT         "$OPT_WEB_HTTP_PORT"    "--web-port"       "管理台 http 端口；开 https 时它只做 301 跳板与设备通路"
  apply_site_opt WEB_HTTPS_PORT        "$OPT_WEB_HTTPS_PORT"   "--web-https-port" "浏览器实际访问的管理台 https 端口"
  apply_site_opt WEB_BIND_HOST         "$OPT_WEB_BIND"         "--web-bind"       "0.0.0.0=内网直接访问，127.0.0.1=只留给宿主反代"
  apply_site_opt WEB_CERT_DIR          "$OPT_CERT_DIR"         "--cert-dir"       "证书目录（web.crt/web.key 放这里，容器只读挂载）"
  apply_site_opt WEB_SERVER_NAME       "$OPT_SERVER_NAME"      "--server-name"     "自签证书的 CN 与 SAN 里的 DNS 项"
  apply_site_opt WEB_SSL_RENEW_DAYS    "$OPT_SSL_DAYS"         "--days"           "剩余有效期低于这个天数才重签"
  if [ -n "$OPT_WEB_HTTPS" ]; then
    if [ "$OPT_WEB_HTTPS" = 1 ]; then apply_site_opt WEB_HTTPS true "--https" "自签 TLS + Secure Cookie，浏览器首次要「继续前往」"; fi
    if [ "$OPT_WEB_HTTPS" = 0 ]; then apply_site_opt WEB_HTTPS false "--no-https" "纯 http，内网明文；口令与令牌都不加密"; fi
  fi
  if [ -n "$OPT_WEB" ]; then
    if [ "$OPT_WEB" = 1 ]; then apply_site_opt WEB_ENABLED true "--web" "起管理台容器（默认就是起）"; fi
    if [ "$OPT_WEB" = 0 ]; then apply_site_opt WEB_ENABLED false "--no-web" "不再起管理台容器，静态资源与反代自己安排"; fi
  fi
}

# 具名卷换成宿主机目录时，目录得先准备成容器写得进的样子：镜像里 platform 用户是
# uid 1001（Dockerfile.api 的 useradd --uid 1001），而 docker 自己建的挂载目录属 root，
# 等到第一包上传才报权限错就太晚了。切位置不搬数据，所以把搬法一并说明。
prepare_blob_dir() {
  local d
  d="$(env_value BLOB_STORAGE_HOST_PATH)"
  [ -n "$d" ] || return 0
  mkdir -p "$d" || die "创建不了附件目录：$d"
  if ! chown 1001:1001 "$d" 2>/dev/null; then
    warn "附件目录 $d 的属主改不成 uid 1001（这一步需要 root）：上传会在运行时报权限错。"
    warn "  手工补一次：chown 1001:1001 $d"
  fi
  if [ -n "$OPT_BLOBS_DIR" ]; then
    warn "附件位置本次切到了 $d。原先存在具名卷里的文件不会自动搬过来："
    warn "  docker volume ls --filter name=blobs     # 卷名形如 <项目名>_blobs"
    warn "  docker run --rm -v <卷名>:/from -v $d:/to alpine sh -c 'cp -a /from/. /to/'"
    warn "  chown -R 1001:1001 $d"
  fi
  return 0
}

# ── 管理台：证书、起栈、就绪、定时重签 ──────────────────────────────────
# 三条约束决定了这一节的形状：
#   ① 刷新令牌是 httpOnly Cookie，API 按请求的 X-Forwarded-Proto 决定要不要打上 Secure，
#      所以只有真的从 https 进来才是 Secure Cookie；纯 http 等于口令与令牌在内网裸跑。
#      默认就开 https（自签），不接受的人 --no-https。
#   ② 设备端（HttpClient/SignalR）没有配置自签信任，硬把它赶上 https 会直接连不上，
#      于是站点在 80 上只保留设备通路（/api、/hubs、探针），其余 301 到 443。
#   ③ TLS 只在 nginx 终结，绝不给 Kestrel 加 https 绑定：应用一旦开始处理重定向，
#      明文请求会被转到一个它并不监听的端口上，设备全灭。
# 证书放在仓库之外（默认 /var/lib/appframework-platform/certs）：这份仓库经常整目录 zip
# 到测试机，私钥不该跟着多走一遍。

lan_ips() {  # 本机 IPv4 列表（空格分隔），给证书 SAN 用；挡掉回环与 docker 网桥
  local raw
  raw="$(hostname -I 2>/dev/null || true)"
  [ -n "$raw" ] || raw="$(ip -4 -o addr 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | tr '\n' ' ')"
  printf '%s\n' $raw | grep -vE '^(127\.|172\.(1[6-9]|2[0-9]|3[01])\.)' | sort -u | awk 'NR<=8' | tr '\n' ' '
}

lan_ip() {  # 首个像样的本机地址，只用于打印「内网可直接访问的那个网址」
  local first
  first="$(lan_ips | awk '{print $1}')"
  printf '%s' "${first:-127.0.0.1}"
}

web_crt() { printf '%s/web.crt' "$WEB_CERT_DIR"; }
web_key() { printf '%s/web.key' "$WEB_CERT_DIR"; }

# 浏览器实际访问管理台用的地址（https 关掉时就是 80 那个端口）
web_base_url() {
  if [ "$WEB_HTTPS_ON" = 1 ]; then printf 'https://localhost:%s' "$WEB_HTTPS_PORT"
  else printf 'http://localhost:%s' "$WEB_HTTP_PORT"; fi
}

# 判定一个目录能不能用，最可靠的办法是真写一次：`-w` 在 NFS/CIFS 上会说谎，
# `install -d -m 700` 在只认 NTFS ACL 的机器上会因为改不动权限而整体失败——
# 那两种情况都把「能写」误判成「不能写」，接着把证书搬到别处去，比原目录难看多了。
web_dir_usable() {
  local probe
  mkdir -p "$WEB_CERT_DIR" 2>/dev/null || return 1
  probe="$WEB_CERT_DIR/.write-probe.$$"
  : > "$probe" 2>/dev/null || return 1
  rm -f "$probe" 2>/dev/null
  chmod 700 "$WEB_CERT_DIR" 2>/dev/null || warn "证书目录 $WEB_CERT_DIR 改不成 700（文件系统不认 chmod？）：私钥对同机其他用户可读，请确认这台机器的账号边界。"
  return 0
}

prepare_cert_dir() {
  local primary="$WEB_CERT_DIR" fallback="$ROOT_DIR/deploy/certs"
  web_dir_usable && return 0
  if [ "$primary" = "$fallback" ]; then
    die "证书目录 $fallback 写得进去却用不了（探针文件建不出来？）：换个位置，或 bash $SELF up --cert-dir <绝对路径>。"
  fi
  WEB_CERT_DIR="$fallback"
  web_dir_usable || die "连 $fallback 都写不出来：这台机器跑不动 https 部署，请用 sudo 跑，或 bash $SELF up --cert-dir <能写的绝对路径>。"
  log "写不进 $primary（需要 root 或那个目录不可写）→ 证书改用 $fallback。"
  log "  已把这个位置写回 $ENV_FILE 的 WEB_CERT_DIR，compose 挂载才会跟脚本看到同一个目录；"
  log "  以后想让 root 跑回默认位置：bash $SELF up --cert-dir $DEFAULT_CERT_DIR。"
  env_set WEB_CERT_DIR "$fallback"
}

cert_is_selfsigned() {  # subject 与 issuer 同一个串才算自签；CA 签的那本归现场自己续
  local sub iss
  sub="$(openssl x509 -in "$(web_crt)" -noout -subject 2>/dev/null | cut -d= -f2-)"
  iss="$(openssl x509 -in "$(web_crt)" -noout -issuer 2>/dev/null | cut -d= -f2-)"
  [ -n "$sub" ] && [ "$sub" = "$iss" ]
}

cert_pair_ok() {  # crt 与 key 是不是同一把（放错文件比证书过期更难查）
  local a b
  a="$(openssl x509 -in "$(web_crt)" -noout -pubkey 2>/dev/null)"
  b="$(openssl pkey -in "$(web_key)" -pubout 2>/dev/null)"
  [ -n "$a" ] && [ "$a" = "$b" ]
}

cert_days_left() {  # 打印剩余天数；算不出来返回 1（date 不认这格式时不阻断部署）
  local end epoch now
  end="$(openssl x509 -in "$(web_crt)" -noout -enddate 2>/dev/null | cut -d= -f2-)" || return 1
  [ -n "$end" ] || return 1
  epoch="$(date -u -d "$end" +%s 2>/dev/null)" || return 1
  now="$(date -u +%s)"
  printf '%s' $(( (epoch - now) / 86400 ))
}

cert_expiry_text() {
  openssl x509 -in "$(web_crt)" -noout -enddate 2>/dev/null | cut -d= -f2- || printf '未知'
}

# 证书 SAN 里有没有当前这个访问地址。IP 是 DHCP 分出来的，换网段以后浏览器就会改口
# 说证书不对——这件事在部署当场说出来，比让人对着红叉猜要划算。
cert_covers_addr() {
  local san addr="$1"
  # openssl 的 -text 把 SAN 排成 "IP Address:10.0.0.7, DNS:foo" 且会折行；去掉空白并统一成
  # "IP:" 前缀再比，免得换一版 openssl 就永远「没覆盖」。
  san="$(openssl x509 -in "$(web_crt)" -noout -text 2>/dev/null | tr -d ' \n' | sed 's/IPAddress:/IP:/g')"
  [ -n "$san" ] || return 0   # 读不出来就不拦，交给浏览器判断
  case "$san" in *"IP:${addr}"*|*"DNS:${addr}"*) return 0 ;; esac
  return 1
}

web_sign_selfsigned() {
  command -v openssl >/dev/null 2>&1 || die "没有 openssl，签不出自签证书：装一个（apt install openssl），或者 bash $SELF up --no-https 先纯 http 跑。"
  local cn san ips ip cf
  cn="$WEB_SERVER_NAME"
  # 手工改过的 .env 里塞了换行或方括号，就会把 openssl 那份配置顶成几条别的指令——
  # 签出来的证书跟想的完全不是一回事，还很难查。只认主机名字符。
  case "$cn" in
    *[!A-Za-z0-9._-]*) die "$ENV_FILE 里 WEB_SERVER_NAME 有非法字符（只认字母、数字、点、下划线、连字符）：$cn" ;;
  esac
  case "$cn" in ''|_) cn="$(hostname -f 2>/dev/null || hostname 2>/dev/null || printf 'appframework-platform')" ;; esac
  ips="$(lan_ips)"
  # 现代浏览器只看 SAN（CN 不参与主机名校验），而内网现场大多是用 IP 直连的，
  # 所以 DNS 与 IP 都要写；--server-name 直接给成 IP 时也别再塞一条没用的 DNS 项。
  san="DNS:localhost,DNS:appframework-platform.local"
  case "$cn" in
    *[0-9]*.[0-9]*.[0-9]*.[0-9]*) san="$san,IP:$cn" ;;
    *) san="DNS:$cn,$san" ;;
  esac
  san="$san,IP:127.0.0.1"
  for ip in $ips; do san="$san,IP:$ip"; done

  cf="$WEB_CERT_DIR/web.cnf"
  cat > "$cf" <<EOF
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = ${cn}
[v3]
basicConstraints = CA:FALSE
keyUsage = digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = ${san}
EOF

  # 刻意不用 req -addext：老 OpenSSL（1.1.0）没有那个选项，-config 是通用路径。
  # 397 天贴着 CA/Browser 论坛的有效期上限，免得 Chrome 对长期证书另加限制。
  umask 077
  openssl req -x509 -newkey rsa:2048 -nodes -days 397 \
    -keyout "$(web_key)" -out "$(web_crt)" -config "$cf" >/dev/null 2>&1 \
    || die "自签证书生成失败（openssl req）。"
  chmod 600 "$(web_key)" 2>/dev/null || true
  chmod 644 "$(web_crt)" 2>/dev/null || true
  WEB_CERT_RENEWED=1
  WEB_SSL_SELF=1
  WEB_SSL_KIND="自签（本脚本签发）"
  log "自签证书已生成：$(web_crt)"
  log "  CN=$cn"
  log "  SAN=$san"
  log "  到期 $(cert_expiry_text)；剩余不足 $WEB_SSL_RENEW_DAYS 天时 renew-cert 会重签"
}

# 决定这次要不要签：能复用就复用。每次部署都重签会让所有浏览器刚记住的「继续前往」例外
# 作废，现场看到的是「昨天还能开，今天全红了」，而这份证书根本没有可信链可言。
ensure_web_cert() {
  local left
  prepare_cert_dir
  if [ -f "$(web_crt)" ] && [ -f "$(web_key)" ]; then
    cert_pair_ok || die "$(web_crt) 与 $(web_key) 不像是同一把密钥：换个文件放错了位置。
  要么把这一对补全，要么清空 $WEB_CERT_DIR 让脚本重新自签（bash $SELF up）。"
    if ! cert_is_selfsigned; then
      WEB_SSL_SELF=0
      WEB_SSL_KIND="现场自备证书（非自签，本脚本不动它）"
      left="$(cert_days_left || true)"
      log "当前证书不是本脚本签的（CA/certbot 签发）：不重签，到期 $(cert_expiry_text)。"
      if [ -n "$left" ] && [ "$left" -le "$WEB_SSL_RENEW_DAYS" ]; then
        warn "  只剩 $left 天了：请在你的签发方换新证书，覆盖 $WEB_CERT_DIR 里的 web.crt 与 web.key，"
        warn "  放好以后 reload 让它生效（不用重建容器）："
        warn "  docker compose $(compose_files_display) --env-file \"$ENV_FILE\" exec -T web nginx -s reload"
      fi
      return 0
    fi
    left="$(cert_days_left || true)"
    if [ "$FORCE_SIGN" != 1 ] && [ -n "$left" ] && [ "$left" -gt "$WEB_SSL_RENEW_DAYS" ]; then
      WEB_SSL_SELF=1
      WEB_SSL_KIND="自签（沿用既有证书）"
      log "复用已有自签证书：$(web_crt)（剩 $left 天，到期 $(cert_expiry_text)）。"
      if ! cert_covers_addr "$(lan_ip)"; then
        warn "  证书 SAN 里没有本机现在的地址（$(lan_ips)）：多半是换过网段，浏览器会抱怨证书不匹配。"
        warn "  重签一张：bash $SELF renew-cert --force"
      fi
      return 0
    fi
    log "自签证书剩 ${left:-未知} 天（阈值 $WEB_SSL_RENEW_DAYS 天）→ 重签。"
  elif [ -f "$(web_crt)" ] || [ -f "$(web_key)" ]; then
    die "$(web_crt) 与 $(web_key) 只有一半个：把这一对补全，或者清空 $WEB_CERT_DIR 让脚本重新自签。"
  fi
  web_sign_selfsigned
}

# reload 才是这里真正的生效动作：换证书不惊动任何已开着的页面，也不重启应用容器。
# （管理台容器重启也只会断掉浏览器里那几个页面的会话，设备端直连 API，不受打扰——
#   老服务端那套要小心 SignalR 全断的顾虑在这条链路上不存在。）
reload_web() {
  # 这条只被 renew-cert 调用，而 renew-cert 由 systemd 定时器跑：那条路径上既没探测过
  # compose，也不保证守护进程已经起来。证书此刻已经落盘，reload 只是让它当场生效，
  # 做不到就退回手工命令——别让一次已经成功的续期因为 reload 而整体报错。
  if ! resolve_compose_bin || ! docker info >/dev/null 2>&1; then
    warn "docker 现在不可用：新证书等 bash $SELF up 起来时才生效。"
    return 0
  fi
  setup_compose
  if [ -z "$(compose ps -q web 2>/dev/null || true)" ]; then
    warn "管理台容器没在跑：新证书等 bash $SELF up 起来时才生效。"
    return 0
  fi
  if compose exec -T web nginx -s reload >/dev/null 2>&1; then
    log "✓ nginx 已 reload，新证书当场生效（容器没重启，浏览器已开着的页面不受影响）。"
  else
    warn "nginx reload 没成功（容器可能刚起来还在初始化）：重跑一次 bash $SELF renew-cert，"
    warn "  或者 bash $SELF up 用重建容器的办法加载新证书。"
  fi
}

# root 下顺手装一个每天跑一次的定时器：自签证书只有 397 天，靠人记得「快到期了」
# 这件事从来靠不住。certbot 那类外部证书不归这里管（上面已按非自签跳过）。
install_cert_timer() {
  [ "$WEB_HTTPS_ON" = 1 ] && [ "$WEB_ENABLED_ON" = 1 ] || return 0
  if [ "${WEB_SSL_SELF:-0}" != 1 ]; then
    log "现场自备的证书：不装定时重签（本脚本不替别人签的证书做续期）。"
    return 0
  fi
  if [ "$NO_CERT_TIMER" = 1 ]; then
    CERT_TIMER_STATE="按要求没装（--no-cert-timer）：到期前自己 bash $SELF renew-cert"
    warn "$CERT_TIMER_STATE"
    return 0
  fi
  if ! command -v systemctl >/dev/null 2>&1; then
    CERT_TIMER_STATE="未安装（这台机器没有 systemctl）"
    warn "$CERT_TIMER_STATE：请自己安排一个每天/每周跑一次的 bash $SELF renew-cert。"
    return 0
  fi
  if [ "$(id -u)" != 0 ] || [ ! -d /etc/systemd/system ]; then
    CERT_TIMER_STATE="未安装（不是 root，或这台机器没有 systemd 目录）"
    warn "$CERT_TIMER_STATE：证书只有 397 天，请自己安排一个定期跑的 bash $SELF renew-cert（cron 也行）。"
    warn "  想用 systemd：模板在 $ROOT_DIR/deploy/systemd/，把两份文件里的 __SELF__ 换成"
    warn "  $SELF 后拷进 /etc/systemd/system，再 systemctl daemon-reload && systemctl enable --now $CERT_TIMER_UNIT"
    return 0
  fi
  # 直接改写仓库里那两份模板（占位符换成脚本绝对路径与阈值），而不是在脚本里再抄一份 unit 内容：
  # 两处各写一遍迟早会长歪，而现场手工安装时看的也是这两份文件。
  local tpl body dest
  for tpl in "$CERT_SERVICE_UNIT" "$CERT_TIMER_UNIT"; do
    [ -f "$SYSTEMD_DIR/$tpl" ] || die "缺 systemd 模板：$SYSTEMD_DIR/$tpl"
    body="$(cat "$SYSTEMD_DIR/$tpl")"
    body="${body//__SELF__/$SELF}"
    body="${body//__RENEW_DAYS__/$WEB_SSL_RENEW_DAYS}"
    dest="/etc/systemd/system/$tpl"
    printf '%s\n' "$body" > "$dest" || die "写不进 $dest（需要 root）。"
  done
  systemctl daemon-reload >/dev/null 2>&1 || true
  systemctl enable --now "$CERT_TIMER_UNIT" >/dev/null 2>&1 \
    || { warn "定时器 enable 失败（root 也启不动？）：手工 systemctl enable --now $CERT_TIMER_UNIT"; CERT_TIMER_STATE="安装失败，需手工启用"; return 0; }
  CERT_TIMER_STATE="已启用（每天 06:17 检查，剩余不足 $WEB_SSL_RENEW_DAYS 天才重签）"
  log "定时重签：$CERT_TIMER_STATE"
  log "  查看：systemctl status $CERT_TIMER_UNIT · journalctl -u $CERT_SERVICE_UNIT"
}

build_and_start_web() {
  log "→ 构建管理台镜像"
  if [ "${SKIP_BUILD:-0}" = "1" ]; then
    log "   SKIP_BUILD=1，复用现有镜像（镜像不存在时 compose up 仍会自己构建一次）"
  else
    log "   首次要 pnpm install（几百个包）+ vite build，之后靠层缓存只要 lockfile 不变就快"
    compose build web
  fi
  log "→ 起管理台"
  compose up -d web
  wait_web_ready
}

wait_web_ready() {
  local url probe attempt=0
  url="$(web_base_url)"
  if [ "$WEB_HTTPS_ON" = 1 ]; then probe="$WEB_HTTPS_PORT"; else probe="$WEB_HTTP_PORT"; fi
  log "→ 等管理台 $url（端口 $probe）"
  while [ "$attempt" -lt 30 ]; do
    # -k：自签证书本来就没有可信链，这里要验的是「站点起没起」，不是证书链
    if curl -kfsS --max-time 3 -o /dev/null "$url/" \
       && curl -kfsS --max-time 5 -o /dev/null "$url/ready"; then
      log "✓ 管理台就绪（首页可开，/ready 反代到 API 也通）"
      return 0
    fi
    printf '   还没起来（第 %s 次）\n' "$attempt"
    attempt=$((attempt + 1))
    sleep 2
  done
  compose ps
  log "── web 最近 50 行日志 ──"
  compose logs --tail 50 web || true
  die "管理台在 60 秒内没就绪。常见三条：
  · 证书没就位：站点按 https 渲染但要 /etc/nginx/certs/web.crt|web.key（容器日志第一行就会写）。
      用 bash $SELF up --https 让脚本签一张，或把现场证书放进 WEB_CERT_DIR=$WEB_CERT_DIR。
  · 上游解析不到 api：容器启动顺序上 web 依赖 api，API 容器没起来 nginx 就会退出。
      先 bash $SELF status 看 api，再看 bash $SELF logs api。
  · 端口没发布成功：上面端口预探测被 --skip-preflight 跳过过，或者被别的进程抢了。
  排查用：bash $SELF logs web"
}

health_web() {
  local url fail=0 left
  url="$(web_base_url)"
  # 全程 -k：自签证书本来就没有可信链，这里要问的是「站点起没起、反代通不通」，
  # 不是「谁签的这张证书」。真要验可信链，就把现场 CA 的证书放进 WEB_CERT_DIR。
  if curl -kfsS --max-time 5 -o /dev/null "$url/"; then
    log "✓ 管理台首页      $url/"
  else
    warn "× 管理台首页打不开：$url/（bash $SELF logs web）"
    fail=1
  fi
  # 这一条比首页更要紧：静态文件在磁盘上就能开，/ready 走的是 nginx → api 那段，
  # 它不通的话页面能开但登录一定转圈。
  if curl -kfsS --max-time 5 -o /dev/null "$url/ready"; then
    log "✓ 管理台 → API    $url/ready（同源反代通，刷新 Cookie 才带得回来）"
  else
    warn "× 管理台到 API 的反代不通：$url/ready（api 容器没起来？.env 里 WEB_API_UPSTREAM 指错了？）"
    fail=1
  fi
  if [ "$WEB_HTTPS_ON" = 1 ] && [ -f "$(web_crt)" ]; then
    left="$(cert_days_left || true)"
    log "  证书            到期 $(cert_expiry_text)${left:+，剩 $left 天}（重签：bash $SELF renew-cert）"
    if [ -n "$left" ] && [ "$left" -le "$WEB_SSL_RENEW_DAYS" ]; then
      warn "  ! 已经低于阈值 $WEB_SSL_RENEW_DAYS 天了，还没有可用的定时任务把它换掉：bash $SELF up --https 重装一次定时器"
    fi
  fi
  [ "$fail" = 0 ] || die "管理台这条链路有问题，上面两行指出的是具体哪一段。"
}

# WEB_ENABLED=false 时那两个叠加文件不再挂进 compose，先前起过的容器就成了「孤儿」：
# compose down / up 都不会再管它，现场看到的是端口还占着、镜像还是旧的。按容器名清掉它，
# 只清这一个服务的容器，别人的东西一律不碰。
remove_web_container() {
  local ids
  ids="$(docker ps -aq --filter name=appframework-platform-web 2>/dev/null || true)"
  [ -n "$ids" ] || return 0
  log "WEB_ENABLED=false：清掉先前起过的管理台容器（$(printf '%s' "$ids" | tr '\n' ' ')）。"
  # shellcheck disable=SC2086  # 这里是多个容器 id，故意不按一个词处理
  docker rm -f $ids >/dev/null 2>&1 || warn "有个别容器没删掉（正被别的东西占着？）：docker ps -a 里手工确认。"
}

# 证书那几行只读文件、不改任何东西：info 是只读动作，up 收尾也要复用同一份打印，
# 两处各写一遍迟早会打出不一致的到期时间。
print_web_cert_lines() {
  local kind left san
  printf '  证书目录      %s（web.crt / web.key，容器只读挂载；私钥 600）\n' "$WEB_CERT_DIR"
  if [ ! -f "$(web_crt)" ] || [ ! -f "$(web_key)" ]; then
    printf '  证书          还没生成：bash %s up --https 会签一张自签的\n' "$SELF"
    return 0
  fi
  if ! command -v openssl >/dev/null 2>&1; then
    printf '  证书          本机没有 openssl，读不出到期时间：%s\n' "$(web_crt)"
    return 0
  fi
  if cert_is_selfsigned; then kind="自签（本脚本签发）"; else kind="现场自备（CA/certbot 签发，本脚本不动它）"; fi
  left="$(cert_days_left || true)"
  printf '  证书          %s\n' "$kind"
  printf '                到期 %s%s\n' "$(cert_expiry_text)" "${left:+（剩 $left 天，阈值 $WEB_SSL_RENEW_DAYS 天以下才重签）}"
  san="$(openssl x509 -in "$(web_crt)" -noout -ext subjectAltName 2>/dev/null | awk 'NR>1' | tr -s ' ' | tr -d '\n')"
  [ -n "$san" ] && printf '                SAN %s\n' "$san"
  if [ "$left" != "" ] && [ "$left" -le "$WEB_SSL_RENEW_DAYS" ]; then
    printf '                ! 已经到了该换的时候：bash %s renew-cert\n' "$SELF"
  fi
  printf '  定时重签      %s\n' "$(web_timer_status)"
}

web_timer_status() {
  if [ -n "$CERT_TIMER_STATE" ]; then printf '%s' "$CERT_TIMER_STATE"; return 0; fi
  if ! command -v systemctl >/dev/null 2>&1; then
    printf '这台机器没有 systemd，没装：请自己定期跑 bash %s renew-cert' "$SELF"
    return 0
  fi
  local st
  st="$(systemctl is-enabled "$CERT_TIMER_UNIT" 2>/dev/null || true)"
  case "$st" in
    enabled)  printf '已启用（看下次触发：systemctl list-timers %s）' "$CERT_TIMER_UNIT" ;;
    disabled) printf 'unit 装了但没启用：sudo systemctl enable --now %s' "$CERT_TIMER_UNIT" ;;
    *)        printf '未安装：root 下 bash %s up --https 会顺手装，或照 %s/ 里那两份模板手工装' "$SELF" "$SYSTEMD_DIR" ;;
  esac
}

# 现场自备（CA/certbot 签的）证书不归本脚本续期：覆盖别人的证书，比忘了续期严重得多。
renew_cert() {
  [ -f "$ENV_FILE" ] || die "还没有 $ENV_FILE，看不出证书该放哪、站点是不是开了 https：先跑 bash $SELF up。"
  env_normalize
  load_vars
  [ "$WEB_ENABLED_ON" = 1 ] || die "WEB_ENABLED=false：这套部署里没有管理台容器，证书没地方用。"
  [ "$WEB_HTTPS_ON" = 1 ] || die "WEB_HTTPS=false：站点是纯 http，不加载证书。要开 TLS：bash $SELF up --https"
  command -v openssl >/dev/null 2>&1 || die "没有 openssl：签不出证书（apt install openssl），也读不了到期时间。"

  if [ "$FORCE_SIGN" = 1 ] && [ -f "$(web_crt)" ] && ! cert_is_selfsigned; then
    die "$(web_crt) 不是自签证书（由现场 CA 或 certbot 签发），--force 会把它覆盖掉。
  确实要换成自签：先把 $WEB_CERT_DIR 里的 web.crt 与 web.key 移走，再 bash $SELF renew-cert --force。"
  fi

  ensure_web_cert
  if [ "$WEB_CERT_RENEWED" != 1 ]; then
    if [ "$WEB_SSL_SELF" = 0 ]; then
      log "外部证书：本脚本不动它（到期 $(cert_expiry_text)）。换好后 reload：bash $SELF up 或 nginx -s reload。"
    else
      log "没重签（剩 $(cert_days_left || printf '?') 天，阈值 $WEB_SSL_RENEW_DAYS 天）。"
      log "  现在这张的到期时间是 $(cert_expiry_text)；真要点名换一张：bash $SELF renew-cert --force"
    fi
    return 0
  fi
  reload_web
  warn "换了新证书，所有浏览器记住的「继续前往」例外一律作废：每台机器各点一次才能再进管理台。"
  warn "  （自签证书没有可信链，这是它的固有代价；想彻底没有这一步，就用现场 CA 签一张放进"
  warn "  $WEB_CERT_DIR，脚本会认出它不是自签、不再动它。）"
}

# ── 起栈前的库检查 ──────────────────────────────────────────────────────
# 撞端口是现场最常见的失败，而且要等镜像构建完才在「设置容器网络」那步爆出来
# （首次部署是拉镜像的一分多钟，之后每次也是几十秒），所以先把要发布的端口探一遍，
# 并把对应的可配置选项直接摆出来。探测口径与老脚本一致：ss 优先（还能报占用方），
# 退 netstat，最后退 /dev/tcp——最小安装可能 iproute2 与 net-tools 都没装。
port_free() {  # port_free <端口> → 0 空闲 / 1 有人在听
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    if ss -ltn 2>/dev/null | awk -v p=":$port" '$4 ~ (p "$")' | grep -q .; then return 1; fi
    return 0
  fi
  if command -v netstat >/dev/null 2>&1; then
    if netstat -ltn 2>/dev/null | awk -v p=":$port" '$4 ~ (p "$")' | grep -q .; then return 1; fi
    return 0
  fi
  ! (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null
}

port_holders() {  # best-effort 打印占用方那一行；非 root 时 ss 拿不到进程名
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -ltnp 2>/dev/null | awk -v p=":$port" '$4 ~ (p "$")' | tail -1
  elif command -v netstat >/dev/null 2>&1; then
    netstat -ltnp 2>/dev/null | awk -v p=":$port" '$4 ~ (p "$")' | tail -1
  fi
  return 0
}

# 「这个端口是不是我们自己栈里的容器占的」——升级时旧容器还在跑，端口当然被自己拿着，
# 而 compose 会先停旧容器再建新容器，这不算冲突，不该拦。逐个 id 比对实际发布端口，
# 是因为 compose run 出来的一次性容器也可能出现在同一服务的列表里。
compose_publishes() {  # compose_publishes <服务> <容器内端口> <宿主机端口> → 0 是本栈占的
  local id hp
  for id in $(compose ps -q "$1" 2>/dev/null || true); do
    hp="$(docker inspect --format "{{(index .NetworkSettings.Ports \"$2/tcp\" 0).HostPort}}" "$id" 2>/dev/null || true)"
    [ "$hp" = "$3" ] && return 0
  done
  return 1
}

# https 这条路少了 openssl 是走不通的（签不出、也读不了到期时间），而发现得太晚会很难看：
# API 已经部署好在跑，脚本却在管理台这一步死掉。所以把它挪到所有构建之前。
preflight_https() {
  [ "$WEB_ENABLED_ON" = 1 ] && [ "$WEB_HTTPS_ON" = 1 ] || return 0
  command -v openssl >/dev/null 2>&1 && return 0
  die "这次要起 https 管理台，但本机没有 openssl：签不出证书，也读不懂现成证书的到期时间。
  四条路，任选一条：
  · 装上它：apt install openssl，然后重跑本脚本
  · 先纯 http 跑起来（以后随时 --https 补上）：bash $SELF up --no-https
  · 不要这个容器：bash $SELF up --no-web
  · 证书由现场 CA 提供：把 web.crt 与 web.key 放进 $WEB_CERT_DIR（键 WEB_CERT_DIR），
    但读到期时间仍然要 openssl，这条路上还是得装。"
}

preflight_ports() {
  if [ "$SKIP_PREFLIGHT" = 1 ]; then
    warn "--skip-preflight：跳过端口预探测。端口真被占用时，报错会等到构建以后才出现。"
    return 0
  fi

  if [ "$EMBEDDED_PG" = 1 ]; then
    if ! compose_publishes postgres 5432 "$PG_PUBLISH_PORT" && ! port_free "$PG_PUBLISH_PORT"; then
      die "宿主机 127.0.0.1:$PG_PUBLISH_PORT 已有服务在听，数据库容器起不来。
  占用方：$(port_holders "$PG_PUBLISH_PORT")
  二选一：
  · 复用这台机器上已有的 PostgreSQL（不建容器）：
      bash $SELF up --use-existing-pg \"Host=host.docker.internal;Port=5432;Database=appframework_platform;Username=<既有账号>;Password=<既有口令>\"
  · 仍用容器库，只把运维直连端口挪开：bash $SELF up --pg-port 5434
（API 走容器网络连库，这个端口只是给运维从宿主机 psql 直连排查用的，而且只发布到回环。）"
    fi
  else
    log "复用外部 PostgreSQL：$PG_HOST:$PG_PORT/$PG_DB（用户 $PG_USER），不创建数据库容器。"
    if [ "$PG_HOST" = "postgres" ]; then
      warn "外部库模式但 POSTGRES_HOST 还是 postgres：那是容器服务名，多半是 .env 没改。"
    fi
  fi

  # 一次性迁移容器不发布端口（compose run 的默认行为），所以只有 up 会撞 API 端口
  if [ "$ACTION" = up ] \
     && ! compose_publishes api 8080 "$API_PORT" && ! port_free "$API_PORT"; then
    die "宿主机端口 $API_PORT 已有服务在听，API 容器起不来。
  占用方：$(port_holders "$API_PORT")
  二选一：
  · 换一个对外端口：bash $SELF up --api-port 18080
    （只挪宿主机发布端口，容器内仍是 8080；部署清单与客户端配的服务地址会跟着变。）
  · 先停掉占用 $API_PORT 的进程，再重跑本脚本
（这台机器上已经跑着 nginx 或别的 Web 时，也可以让 API 只绑回环、由反代对外：
  bash $SELF up --api-bind 127.0.0.1，反代 upstream 指 127.0.0.1:$API_PORT。）"
  fi

  # 管理台那两个端口同理，只是这里更常撞到「宿主上已经有一套 nginx 在代理别的应用」：
  # 默认端口刻意选了 8081/8443，就是为了跟别人的 80/443 井水不犯河水。
  if [ "$ACTION" = up ] && [ "$WEB_ENABLED_ON" = 1 ]; then
    local web_port_hint="
  这台机器上通常已经有一套 nginx 在代理别的应用，本栈刻意不碰它，管理台默认发布在
  非标准端口（http $WEB_HTTP_PORT / https $WEB_HTTPS_PORT）。三种走法：
  · 换一对空闲端口：bash $SELF up --web-port 8082 --web-https-port 8444
  · 不要这个容器，管理台继续用宿主 nginx 挂静态资源：bash $SELF up --no-web
    （把 $ROOT_DIR/deploy/nginx/site.d/ 里对应那份的 location 抄进你现有的站点即可；
      /api 必须同源反代到 API——刷新令牌是 httpOnly Cookie，跨源还得 CORS + SameSite=None。）
  · 只想本机可达、由宿主反代对外：bash $SELF up --web-bind 127.0.0.1"
    if ! compose_publishes web 80 "$WEB_HTTP_PORT" && ! port_free "$WEB_HTTP_PORT"; then
      die "宿主机端口 $WEB_HTTP_PORT 已有服务在听，管理台容器起不来。
  占用方：$(port_holders "$WEB_HTTP_PORT")$web_port_hint"
    fi
    if [ "$WEB_HTTPS_ON" = 1 ] \
       && ! compose_publishes web 443 "$WEB_HTTPS_PORT" && ! port_free "$WEB_HTTPS_PORT"; then
      die "宿主机端口 $WEB_HTTPS_PORT 已有服务在听，管理台的 https 起不来。
  占用方：$(port_holders "$WEB_HTTPS_PORT")$web_port_hint"
    fi
  fi
  return 0
}

# ── 迁移：跑完即退的一次性容器 ──────────────────────────────────────────
# 生产不能用 AutoMigrate（多副本会互相抢），所以这里用镜像内同一入口加 --migrate。
run_migrate() {
  log "→ 数据库迁移（一次性容器）"
  compose run --rm --no-deps api --migrate
}

# 嵌入模式先起库并等健康；外部模式没有 postgres 服务，库能不能用由迁移容器判定。
prepare_db() {
  [ "$EMBEDDED_PG" = 1 ] || return 0
  compose up -d postgres
  log "→ 等 PostgreSQL 就绪"
  local id attempt=0
  id="$(compose ps -q postgres)"
  while [ "$attempt" -lt 60 ]; do
    if [ "$(docker inspect --format '{{.State.Health.Status}}' "$id" 2>/dev/null || echo starting)" = "healthy" ]; then
      return 0
    fi
    attempt=$((attempt + 1))
    sleep 2
  done
  die "PostgreSQL 60 次探测内没就绪，看 bash $SELF logs postgres"
}

# 用 API 那条路（TCP + scram）验一次口令，而不是在容器里用 unix socket 连——镜像 initdb 用的是
# --auth-local=trust，本机 socket 连不验口令，那样探出来的「能连」是假的。
# 三行 stdin 依次是口令 / 角色 / 库名：值都不出现在容器侧的命令行上（写 -e PGPASSWORD=… 会被
# 宿主机 ps 看见），也不需要把口令转义进 sh——printf %q 那种写法产出的 $'…' 形式 busybox ash
# 不一定认，会把口令读歪、反过来误报 28P01。
db_auth_probe() {
  printf '%s\n%s\n%s\n' "$(env_value POSTGRES_PASSWORD)" "$PG_USER" "$PG_DB"
}

db_auth_exec() {
  db_auth_probe | compose exec -T postgres sh -c \
    'read -r p; read -r u; read -r d; PGPASSWORD="$p" psql -h 127.0.0.1 -U "$u" -d "$d" -tAc "select 1" >/dev/null'
}

db_auth_ok() { db_auth_exec >/dev/null 2>&1; }

# 卷里的角色口令与 .env 不一致（28P01）是重跑部署最常撞的一类：initdb 只在空卷上跑一次，
# 之后 .env 改了、卷里那个角色的口令不会跟着变。与其让迁移容器抛一叠 Npgsql 栈，
# 不如在跑迁移之前就说清楚是这件事、以及哪条路能修。
check_db_auth() {
  [ "$EMBEDDED_PG" = 1 ] || return 0
  if [ "$SKIP_PREFLIGHT" = 1 ]; then
    warn "--skip-preflight：跳过库口令预检（28P01 这类不一致会等到迁移容器里才报）。"
    return 0
  fi
  local out
  out="$(db_auth_exec 2>&1)" && return 0
  case "$out" in
    *"password authentication failed"*)
      die "库容器本身是好的，但 pgdata 卷里角色 $PG_USER 的口令与 $ENV_FILE 的 POSTGRES_PASSWORD 对不上（28P01）。
  多半是 .env 被删过或重新生成过，而数据卷还在：initdb 只在空卷上跑一次，之后改 .env 不会跟着改库里的角色口令。
  两条路：
  · 把卷里的角色口令对到 .env（不动库里任何数据，推荐）：
      bash $SELF fix-pg-password
  · 这个库确实不要了，连卷一起重来（**库里数据与附件卷全部删除**，且只在嵌入模式下成立）：
      docker compose $(compose_files_display) --env-file \"$ENV_FILE\" down -v
      bash $SELF up" ;;
    *)
      warn "嵌入库连接预检没通过（迁移会给具体错误）：$(printf '%s\n' "$out" | tail -1)" ;;
  esac
  return 0
}

# 修复动作：只 ALTER ROLE，把卷里那个角色的口令对到 .env 上。走容器内 unix socket（trust），
# 所以不需要旧口令；不改 .env、不动任何数据，也不受理外部库（那是别人的实例，不该由本脚本来改）。
fix_pg_password() {
  local pass esc
  [ "$EMBEDDED_PG" = 1 ] || die "当前是外部库模式：本脚本不改别人实例里的角色口令。请在那套 PG 上自己 ALTER ROLE，或把 .env 里的 POSTGRES_PASSWORD 改成它的既有口令。"
  pass="$(env_value POSTGRES_PASSWORD)"
  [ -n "$pass" ] || die "$ENV_FILE 里 POSTGRES_PASSWORD 是空的：先填上要与库里的角色对上哪一个口令。"
  esc="${pass//\'/\'\'}"   # SQL 单引号转义：' → ''（已验证 a'b 变 a''b）
  require_docker
  prepare_db
  compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$PG_USER" -d postgres \
    -c "ALTER ROLE \"$PG_USER\" WITH PASSWORD '$esc'" \
    || die "ALTER ROLE 失败：确认库容器在跑（bash $SELF status），以及 $PG_USER 是这套库的初始超级用户。"
  if db_auth_ok; then
    log "✓ 已把 pgdata 卷里角色 $PG_USER 的口令对到 $ENV_FILE（按 API 那条 TCP 路径验证通过），库里数据没动。"
    log "  继续部署：bash $SELF up"
  else
    die "口令已改但 TCP 登录仍失败：多半是 .env 里 POSTGRES_USER 与卷里初始化的角色不是同一个（换过角色名就会这样），或 pg_hba 被改过。"
  fi
}

# 引导口令进不去管理台是这一类：种子只在 admin 账号**不存在**时才写口令（改过口令的人不该被
# 重启盖回去），所以「.env 换过一次而数据卷还是老的」「首装时 SEED_ADMIN_PASSWORD 是空的
# （env.example 里就留空）」都会留下一个印在单子上却对不上库的口令，而且它不会自己好。
# 修法是把 .env 里那份强行按回去：一次性容器、跑完即退，口令走环境变量不进 argv。
reset_admin_password() {
  local pass
  pass="$(env_value SEED_ADMIN_PASSWORD)"
  if [ -z "$pass" ]; then
    pass="Adm-$(rand_hex 12)-7"
    env_set SEED_ADMIN_PASSWORD "$pass"
    log "SEED_ADMIN_PASSWORD 本来是空的（手工 cp env.example 就是这种）：已生成一个并写回 .env。"
  fi
  require_docker
  prepare_db
  check_db_auth
  log "→ 一次性容器里把 admin 的口令按回 $ENV_FILE 那份（旧会话全部作废）"
  if ! compose run --rm --no-deps api --reset-admin-password; then
    warn "重置没成功：报错在上面。几条常见原因——"
    warn "  · 库没起来或迁移还没跑完（users 表还不存在）：先 bash $SELF health 或 bash $SELF migrate"
    warn "  · API 镜像还不存在（从没成功部署过）：先 bash $SELF up"
    warn "  · .env 与数据卷的库口令对不上（28P01）：bash $SELF fix-pg-password"
    warn "  · 报 No service for type ... / Unrecognized option：镜像还是旧代码，"
    warn "    这个动作本身不带编译，先 bash $SELF up 重建再重试"
    die "管理员口令重置失败。"
  fi
  log "✓ admin 的口令已对回 $ENV_FILE 里的 SEED_ADMIN_PASSWORD。"
  log "  登录：admin / $pass"
  log "  进去会立刻要求改密（改完这份引导口令就失效）；它名下所有旧会话已作废，别的用户不受影响。"
  return 0
}

# 迁移失败时按模式给方向：这两种模式的失败原因完全不是一回事，混在一段里现场没法用。
migrate_failed_hint() {
  if [ "$EMBEDDED_PG" = 1 ]; then
    warn "嵌入库迁移失败，多半是这两条："
    warn "  · pgdata 卷是早先初始化的、里面那个角色口令与现在的 .env 不一致"
    warn "    （删过 .env 或换过机器就会这样）。对上新口令即可，不必清库："
    warn "    bash $SELF fix-pg-password   # 只 ALTER ROLE 把卷里的口令对到 .env，数据不动"
    warn "    bash $SELF logs postgres     # 想先看库本身日志的话"
    warn "  · 库还没初始化完成就被抢占：重跑 bash $SELF migrate"
  else
    warn "外部库迁移失败，按这四条查："
    warn "  · 库与账号是否存在：$PG_DB / $PG_USER（本脚本不会替别人的实例建库）"
    warn "  · 该账号对 $PG_DB 的 CONNECT 权限"
    warn "  · pg_hba.conf 是否放行容器网段（默认 172.16.0.0/12），只听 127.0.0.1 的库连不上"
    warn "  · 宿主机自装的 PG：POSTGRES_HOST 必须是 host.docker.internal，不是 127.0.0.1"
    warn "  建新库：sudo -u postgres createuser -R \"$PG_USER\" && sudo -u postgres createdb -O \"$PG_USER\" \"$PG_DB\""
  fi
}

# ── 等 API 就绪 ─────────────────────────────────────────────────────────
wait_ready() {
  log "→ 等 $base/ready"
  local attempt=0
  while [ "$attempt" -lt 90 ]; do
    if curl -fsS --max-time 3 "$base/ready" >/dev/null 2>&1; then
      log "✓ 就绪"
      return 0
    fi
    if curl -fsS --max-time 3 "$base/healthz" >/dev/null 2>&1; then
      printf '   进程活着但就绪检查未通过（第 %s 次）\n' "$attempt"
    else
      printf '   还没起来（第 %s 次）\n' "$attempt"
    fi
    attempt=$((attempt + 1))
    sleep 2
  done
  compose ps
  log "── api 最近 50 行日志 ──"
  compose logs --tail 50 api || true
  die "API 在 180 秒内没有就绪。"
}

# ── 部署信息汇总（现场要抄的东西；事后重取用 info，别靠重定向 up 的输出） ─
print_info() {
  local admin jwt priv pub fp="" keyid db_pass
  local metrics_note="（匿名可读，公网可达时请在 .env 设 METRICS_SCRAPE_TOKEN）"
  admin="$(env_value SEED_ADMIN_PASSWORD)"
  jwt="$(env_value JWT_SIGNING_KEY)"
  priv="$(env_value LICENSING_SIGNING_KEY_PKCS8)"
  pub="$(env_value LICENSING_PUBLIC_KEY_SPKI_BASE64)"
  db_pass="$(env_value POSTGRES_PASSWORD)"
  if [ -n "$(env_value METRICS_SCRAPE_TOKEN)" ]; then metrics_note="（需带 X-Metrics-Token）"; fi

  printf '\n=============== 平台服务端部署信息 ===============\n'
  if [ "$EMBEDDED_PG" = 1 ]; then
    printf '【数据库】嵌入容器（数据在 pgdata 卷里，重跑部署不动它）\n'
    printf '  容器内连接  Host=postgres Port=%s Database=%s User=%s\n' "$PG_PORT" "$PG_DB" "$PG_USER"
    printf '  宿主机直连  127.0.0.1:%s（只发布到回环，供运维排查用）\n' "$PG_PUBLISH_PORT"
  else
    printf '【数据库】复用外部 PostgreSQL（不建容器，本栈不管它的启停）\n'
    printf '  连接        Host=%s Port=%s Database=%s User=%s\n' "$PG_HOST" "$PG_PORT" "$PG_DB" "$PG_USER"
  fi
  if [ -n "$db_pass" ]; then
    printf '  口令        ***（全文需 --show-secrets；泄露=可任意读写业务库）\n'
  else
    printf '  口令        （.env 里为空）\n'
  fi

  printf '\n【访问】\n'
  printf '  API         %s/api/v1\n' "$base"
  printf '  对外发布    %s:%s → 容器内 8080（挪端口 --api-port，只给反代 --api-bind 127.0.0.1）\n' "$API_BIND_HOST" "$API_PORT"
  if [ -n "$BLOB_PATH" ]; then
    printf '  附件存储    宿主机目录 %s（容器以 uid 1001 读写）\n' "$BLOB_PATH"
  else
    printf '  附件存储    具名卷 blobs（在 docker 卷目录下；要放数据盘用 --blobs-dir <绝对路径>）\n'
  fi
  printf '  存活/就绪   %s/healthz · %s/ready\n' "$base" "$base"
  printf '  指标        %s/metrics %s\n' "$base" "$metrics_note"
  printf '  接口文档    %s/scalar/v1 · %s/openapi/v1.json\n' "$base" "$base"
  if [ "$WEB_ENABLED_ON" = 1 ]; then
    printf '  管理台      见下面【管理台】那份（容器已起，不用再 pnpm dev）\n'
  else
    printf '  管理台      不起容器（WEB_ENABLED=false）：本机开发用 cd %s/web && pnpm dev，\n' "$ROOT_DIR"
    printf '              对外交付请自己挂静态资源——/api 必须同源反代到 %s，\n' "$base"
    printf '              刷新令牌是 httpOnly Cookie，跨源部署还要 CORS + SameSite=None。\n'
    printf '              现成的 location 抄 %s/deploy/nginx/site.d/ 里对应那份即可。\n' "$ROOT_DIR"
  fi

  printf '\n【管理台】\n'
  if [ "$WEB_ENABLED_ON" != 1 ]; then
    printf '  未启用        .env 里 WEB_ENABLED=false；要起：bash %s up --web\n' "$SELF"
  else
    local wurl
    wurl="$(web_base_url)"
    printf '  访问地址      %s\n' "$wurl"
    if [ "$WEB_HTTPS_ON" = 1 ]; then
      printf '  内网地址      https://%s:%s（自签证书，浏览器首次要点「继续前往」；换 IP 后要重签）\n' "$(lan_ip)" "$WEB_HTTPS_PORT"
      printf '                本机回环 %s；同一容器的明文端口 http://localhost:%s 只留给设备通路（/api、/hubs）与 301 跳板\n' "$wurl" "$WEB_HTTP_PORT"
      printf '                设备端不上 https：客户端没有配自签信任，硬赶上会直接连不上\n'
    else
      printf '  内网地址      http://%s:%s（纯 http：口令与令牌在内网裸跑，随时 bash %s up --https 补上）\n' "$(lan_ip)" "$WEB_HTTP_PORT" "$SELF"
    fi
    printf '  站点配置      %s/deploy/nginx/site.d/（改了要重新构建镜像；开关 https 只用重建容器）\n' "$ROOT_DIR"
    printf '  扫码审批      %s/qr-login/<请求id>（二维码里写的就是这个链接）\n' "${QR_APPROVAL_URL_BASE:-未设置}"
    printf '                手机要能打开它：换网段/上域名后改 .env 的 QR_APPROVAL_URL_BASE 再 bash %s up\n' "$SELF"
    printf '  上游          %s（/api、/hubs 反代到这里；API 装在别处时改 .env 的 WEB_API_UPSTREAM）\n' "${WEB_API_UPSTREAM:-api:8080}"
    printf '  发布端口      http %s:%s → 容器内 80\n' "$WEB_BIND_HOST" "$WEB_HTTP_PORT"
    if [ "$WEB_HTTPS_ON" = 1 ]; then
      printf '              https %s:%s → 容器内 443（浏览器实际访问的就是这个端口，301 也跳它）\n' \
        "$WEB_BIND_HOST" "$WEB_HTTPS_PORT"
    fi
    printf '              只想给本机用：bash %s up --web-bind 127.0.0.1\n' "$SELF"
    if [ "$WEB_HTTPS_ON" = 1 ]; then print_web_cert_lines; fi
    printf '  与宿主 nginx  本栈不动它，也不在 443 上抢 default_server（那会把别的应用的站点顶掉）。\n'
    printf '                要并进现有站点：抄 site.d 里的 location，证书目录指过来即可。\n'
  fi

  printf '\n【管理员】\n'
  if [ -n "$admin" ]; then
    printf '  引导口令    admin / %s\n' "$admin"
    printf '              首次登录会被强制改密，改完这个引导口令即失效、全部会话作废。\n'
    printf '              注意它只对得上 .env：库里那个 admin 若是更早一次部署建的、或已改过密，\n'
    printf '              这份口令进不去。强行对回：bash %s reset-admin-password\n' "$SELF"
  else
    printf '  未设置（SEED_ADMIN_PASSWORD 留空 = 不创建管理员，内置角色与演示应用照常就绪）\n'
    printf '              要现在建一个：bash %s reset-admin-password（自动生成口令并写回 .env）\n' "$SELF"
  fi

  printf '\n【令牌】\n'
  if [ "$SHOW_SECRETS" = 1 ]; then
    printf '  JWT 签名密钥 %s\n' "${jwt:-（空）}"
  else
    printf '  JWT 签名密钥 见 %s 的 JWT_SIGNING_KEY（默认不打印，需全文加 --show-secrets）\n' "$ENV_FILE"
  fi

  printf '\n【授权签发】\n'
  if [ -n "$priv" ]; then
    if [ -z "$pub" ]; then pub="$(license_pub_from_priv "$priv" || true)"; fi
    if [ -n "$pub" ] && command -v openssl >/dev/null 2>&1; then
      fp="$(license_fingerprint "$pub")"
    fi
    if [ -n "$fp" ]; then
      keyid="lk-${fp:0:16}"
      printf '  状态        managed\n'
      printf '  keyId       %s\n' "$keyid"
      printf '  公钥指纹    SHA256:%s\n' "$fp"
      printf '  公钥 SPKI   %s\n' "$pub"
      printf '              客户端要把这把公钥列为受信密钥，否则一律按 unmanaged 处理；\n'
      printf '              服务端授权状态接口也能把 keyId 与公钥读给你（公钥不是秘密）。\n'
    elif [ -n "$pub" ]; then
      printf '  状态        managed（本机没有 openssl，指纹算不出；公钥 SPKI=%s）\n' "$pub"
    else
      printf '  状态        私钥非空但派生不出公钥——不是 ECDSA P-256 PKCS#8？请人工核对\n'
    fi
    if [ "$SHOW_SECRETS" = 1 ]; then
      printf '  签发私钥    %s\n' "$priv"
    fi
  else
    printf '  状态        unmanaged（未配 LICENSING_SIGNING_KEY_PKCS8：不签发快照/离线码，客户端不受限）\n'
  fi
  if [ -n "${LICENSE_STATE:-}" ]; then
    printf '  本次密钥    %s\n' "$LICENSE_STATE"
  fi

  printf '\n【凭据与运维】\n'
  printf '  凭据文件    %s（600，已被 .gitignore 忽略）\n' "$ENV_FILE"
  if [ "${ENV_IS_NEW:-0}" = 1 ]; then
    printf '  本清单里的密钥与口令为本次新生成（首次部署），请交给密钥管理系统保管。\n'
  else
    printf '  本清单里的密钥与口令沿用既有 %s（重跑部署不重置），原先留空的已按上面的说明补写。\n' "$ENV_FILE"
  fi
  printf '  只给运维：JWT 签名密钥泄露 = 任何人可伪造管理员令牌，不要抄进交给客户的记录。\n'
  printf '  现场可调    改 %s 或带上对应选项重跑 up：\n' "$ENV_FILE"
  printf '              API_PORT/--api-port · API_BIND_HOST/--api-bind · POSTGRES_*/--use-existing-pg\n'
  printf '              POSTGRES_PUBLISH_PORT/--pg-port · BLOB_STORAGE_HOST_PATH/--blobs-dir\n'
  printf '              WEB_ENABLED/--web|--no-web · WEB_HTTP_PORT/--web-port · WEB_HTTPS/--https\n'
  printf '              WEB_HTTPS_PORT/--web-https-port · WEB_BIND_HOST/--web-bind · WEB_CERT_DIR/--cert-dir\n'
  printf '              WEB_SERVER_NAME/--server-name · WEB_SSL_RENEW_DAYS/--days · WEB_API_UPSTREAM\n'
  printf '              METRICS_SCRAPE_TOKEN · WEBHOOKS_DISPATCHER_ENABLED · JWT_ACCESS_TOKEN_MINUTES\n'
  printf '  日常动作  bash %s status · logs [api|web|postgres] · health · migrate · up · info\n' "$SELF"
  printf '              证书续期：bash %s renew-cert [--force]\n' "$SELF"
  printf '              进不去管理台（引导口令对不上库）：bash %s reset-admin-password\n' "$SELF"
  printf '  下次升级  重跑 bash %s up（库、卷、本清单里的密钥均保留）\n' "$SELF"
  printf '==================================================\n'
  printf '\n验证链路（服务端在跑就能跑通）：\n'
  printf '  PLATFORM_URL=%s PLATFORM_PASSWORD='"'"'<.env 里的 SEED_ADMIN_PASSWORD>'"'"' \\\n' "$base"
  printf '    dotnet run --project %s/samples/ConsoleQuickstart -c Release\n' "$ROOT_DIR"
}

# ── uninstall：开发期拆回「从没部署过」 ─────────────────────────────────
# 与 down 的分界是「环境状态留不留」：down 只停容器，下次 up 接着用旧库、旧口令、旧证书；
# uninstall 删容器/网络/卷/镜像/.env/自签证书/重签定时器，下一把 up 是全新的现场。
# 外部库是别人的实例：默认只拆本栈容器，不 DROP / ALTER，只把该手工执行的 SQL 打出来；
# 显式 --drop-external-db 才清 .env 指定的那一个库（DROP + 重建同名空库）。
UNINSTALL_PROJECT="appframework-platform"

# --purge-blobs 删的是 .env 里配的任意绝对路径，必须在动容器之前先把明显越界的挡掉：
# 一旦这个路径是仓库的祖先目录，后面 rm -rf 会把代码树一起带走。
check_purge_blob_path() {
  [ "$UNINSTALL_PURGE_BLOBS" = 1 ] || return 0
  case "${BLOB_PATH:-}" in
    '') warn "--purge-blobs：.env 里没有 BLOB_STORAGE_HOST_PATH，附件在具名卷 blobs 里，已随卷删除。" ;;
    /|/home|/root|/var|/usr|/etc|/opt|/tmp)
      die "拒绝删除 $BLOB_PATH：这是系统/家目录本身，不可能是附件目录。" ;;
    /*)
      case "$BLOB_PATH" in
        */../*|*/..) die "拒绝删除 $BLOB_PATH：路径里带 ..，规范化后可能指向仓库或系统目录。" ;;
      esac
      case "$ROOT_DIR/" in
        "$BLOB_PATH"/*) die "拒绝删除 $BLOB_PATH：本仓库就在它里面，删了会连代码一起带走。" ;;
      esac
      ;;
    *) die "--purge-blobs 指向的不是绝对路径：$BLOB_PATH" ;;
  esac
}

confirm_uninstall() {
  local ans db
  if [ "$UNINSTALL_HAS_ENV" = 1 ] && [ "$EMBEDDED_PG" = 0 ]; then
    if [ "$UNINSTALL_DROP_EXTERNAL_DB" = 1 ]; then
      db="容器与网络；外部库 ${PG_HOST:-?}:${PG_PORT:-?}/${PG_DB:-?} 连数据一起清"
    else
      db="容器与网络；外部库 ${PG_HOST:-?}:${PG_PORT:-?}/${PG_DB:-?} 不动"
    fi
  elif [ "$UNINSTALL_HAS_ENV" = 1 ]; then
    db="容器、网络、数据卷（嵌入库 pgdata 整库 + 附件 blobs）"
  else
    db="本项目的容器、网络、数据卷（pgdata / blobs，如果存在）"
  fi
  if [ "$UNINSTALL_YES" = 1 ]; then return 0; fi
  [ -t 0 ] || die "没有交互终端，不替你删东西：确认无误后加 --yes 重跑（bash $SELF uninstall --yes）。"
  printf '即将删除（开发期重置，不可恢复）：\n'
  printf '  · %s\n' "$db"
  if [ "$UNINSTALL_KEEP_IMAGES" != 1 ]; then printf '  · 本栈镜像（下次 up 重新构建）\n'; fi
  if [ "$UNINSTALL_KEEP_ENV" != 1 ]; then
    printf '  · deploy/.env（口令/JWT/授权私钥；下次 up 重新生成，已签发的离线授权码会失效）\n'
  fi
  if [ "$UNINSTALL_KEEP_CERTS" != 1 ]; then
    printf '  · 自签证书与每日重签定时器（浏览器记住的「继续前往」例外会作废）\n'
  fi
  if [ "$UNINSTALL_PURGE_BLOBS" = 1 ] && [ -n "${BLOB_PATH:-}" ]; then
    printf '  · 宿主机附件目录 %s\n' "$BLOB_PATH"
  fi
  if [ "$UNINSTALL_DROP_EXTERNAL_DB" = 1 ]; then
    printf '  · 外部库 %s:%s/%s（DROP + 重建同名空库）\n' "${PG_HOST:-?}" "${PG_PORT:-?}" "${PG_DB:-?}"
  fi
  printf '继续请输入 yes：'
  read -r ans || true
  case "$ans" in
    yes|YES|Yes) ;;
    *) die "已取消，什么都没删。" ;;
  esac
}

# 只拆本仓库装的那一份 unit：service 里的 ExecStart 必须指向本脚本，别把同名但属于
# 别的仓库/手工复制的定时器一起 disable 掉。
remove_cert_timer_units() {
  local dest svc
  dest="/etc/systemd/system/$CERT_TIMER_UNIT"
  svc="/etc/systemd/system/$CERT_SERVICE_UNIT"
  [ -f "$dest" ] || return 0
  if [ "$(id -u)" != 0 ]; then
    warn "证书重签定时器在 $dest，但不是 root 删不掉：sudo systemctl disable --now $CERT_TIMER_UNIT && sudo rm -f $dest $svc"
    return 0
  fi
  if ! grep -qF "$SELF" "$svc" 2>/dev/null; then
    warn "$CERT_SERVICE_UNIT 不是本仓库装的（ExecStart 没指向 $SELF）：没动它，核对后手工处理。"
    return 0
  fi
  systemctl disable --now "$CERT_TIMER_UNIT" >/dev/null 2>&1 || true
  systemctl stop "$CERT_SERVICE_UNIT" >/dev/null 2>&1 || true
  rm -f "$dest" "$svc"
  systemctl daemon-reload >/dev/null 2>&1 || true
  log "→ 已删除证书重签定时器（$CERT_TIMER_UNIT）"
}

# 外部库清空：只在显式 --drop-external-db 时执行，且只 DROP/CREATE .env 里那一个库。
# 口令走 stdin（不进程容器命令行），库名/用户名先过字符白名单再拼进 SQL，避免注入。
check_drop_external_db() {
  [ "$UNINSTALL_DROP_EXTERNAL_DB" = 1 ] || return 0
  [ "$UNINSTALL_HAS_ENV" = 1 ] || die "--drop-external-db 需要 deploy/.env 提供外部库连接信息。"
  [ "$EMBEDDED_PG" = 0 ] || die "--drop-external-db 只用于外部库模式；嵌入库的 pgdata 会随数据卷删除。"
  case "$PG_DB" in ''|*[!A-Za-z0-9_]*) die "库名只支持字母、数字、下划线，拒绝拼进 SQL：$PG_DB" ;; esac
  case "$PG_USER" in ''|*[!A-Za-z0-9_]*) die "用户名只支持字母、数字、下划线，拒绝拼进 SQL：$PG_USER" ;; esac
  [ -n "$(env_value POSTGRES_PASSWORD)" ] || die "$ENV_FILE 里 POSTGRES_PASSWORD 是空的，连不上外部库。"
}

drop_external_database() {
  local pass
  [ "$UNINSTALL_DROP_EXTERNAL_DB" = 1 ] || return 0
  pass="$(env_value POSTGRES_PASSWORD)"
  log "→ 清空外部库 $PG_DB（$PG_HOST:$PG_PORT）：DROP DATABASE IF EXISTS + CREATE DATABASE"
  if ! printf '%s\n%s\n' "$pass" "$PG_DB" | docker run --rm -i \
        --add-host=host.docker.internal:host-gateway \
        -e PGHOST="$PG_HOST" -e PGPORT="$PG_PORT" -e PGUSER="$PG_USER" \
        postgres:17-alpine sh -c '
          IFS= read -r pw; IFS= read -r db
          PGPASSWORD="$pw" psql -v ON_ERROR_STOP=1 -d postgres \
            -c "DROP DATABASE IF EXISTS \"$db\" WITH (FORCE);" \
            -c "CREATE DATABASE \"$db\" OWNER \"$PGUSER\";"
        '; then
    die "外部库清空失败（库可能没动或只剩一半）。常见原因：账号没有 CREATEDB 权限、宿主库只监听回环导致容器连不上、拉不到 postgres:17-alpine。
  手工执行：psql -h <宿主地址> -U $PG_USER -d postgres -c 'DROP DATABASE IF EXISTS \"$PG_DB\" WITH (FORCE);' -c 'CREATE DATABASE \"$PG_DB\" OWNER \"$PG_USER\";'"
  fi
  log "✓ 外部库已重建为空库（下一次 up 会重新跑迁移）"
}

uninstall_stack() {
  local ids nets vols imgs crt key

  require_docker
  check_purge_blob_path
  check_drop_external_db
  confirm_uninstall

  if [ "$UNINSTALL_HAS_ENV" = 1 ]; then
    log "→ 停栈并删除 compose 声明的数据卷"
    # down 失败不阻断：下面按项目标签兜底扫一遍，比停在半路更接近「拆干净」这个目标。
    compose down --volumes --remove-orphans || warn "compose down 没干净退出，残留交给下面按标签清理。"
  else
    log "没有 $ENV_FILE：跳过 compose down，直接按项目标签清理残留。"
  fi

  # compose down 只认得当前 .env 选中的那几份叠加文件：换过模式（嵌入库↔外部库、
  # 开过再关管理台）留下的容器/网络/卷它不管，所以再按项目标签完整扫一遍。
  ids="$(docker ps -aq --filter "label=com.docker.compose.project=$UNINSTALL_PROJECT" 2>/dev/null || true)"
  if [ -n "$ids" ]; then
    log "→ 清理残留容器"
    # shellcheck disable=SC2086  # 多个容器 id，故意按词展开
    docker rm -f $ids >/dev/null 2>&1 || warn "有个别容器没删掉：docker ps -a 里核对后再 docker rm。"
  fi

  # 连接都断了再动库；失败就直接停住，别把本地产物删了却留下一个半清的库。
  drop_external_database

  nets="$(docker network ls -q --filter "label=com.docker.compose.project=$UNINSTALL_PROJECT" 2>/dev/null || true)"
  if [ -n "$nets" ]; then
    # shellcheck disable=SC2086  # 多个网络 id
    docker network rm $nets >/dev/null 2>&1 || true
  fi

  vols="$(docker volume ls -q --filter "label=com.docker.compose.project=$UNINSTALL_PROJECT" 2>/dev/null || true)"
  # 兜底按 compose 的命名规则再点名两个：条目少的时候标签未必都打上，名字是确定的。
  vols="$vols $(docker volume ls -q --filter "name=${UNINSTALL_PROJECT}_pgdata" 2>/dev/null || true)"
  vols="$vols $(docker volume ls -q --filter "name=${UNINSTALL_PROJECT}_blobs" 2>/dev/null || true)"
  vols="$(printf '%s\n' $vols | sort -u | tr '\n' ' ')"
  if [ -n "${vols// /}" ]; then
    log "→ 删除数据卷"
    # shellcheck disable=SC2086  # 多个卷名
    docker volume rm $vols >/dev/null 2>&1 || warn "有个别卷没删掉（还被别的容器用着？）：docker volume ls 里核对后再 docker volume rm。"
  fi

  if [ "$UNINSTALL_KEEP_IMAGES" != 1 ]; then
    imgs="$( { docker images -q --filter "label=com.docker.compose.project=$UNINSTALL_PROJECT" 2>/dev/null || true
               docker images -q 'appframework-platform-*' 2>/dev/null || true; } | sort -u | tr '\n' ' ')"
    if [ -n "${imgs// /}" ]; then
      log "→ 删除本栈镜像"
      # shellcheck disable=SC2086  # 多个镜像 id
      docker image rm -f $imgs >/dev/null 2>&1 || warn "有个别镜像没删掉：docker image ls 里核对后再 docker image rm。"
    fi
  fi

  if [ "$UNINSTALL_KEEP_CERTS" != 1 ]; then
    remove_cert_timer_units
    crt="$(web_crt)"; key="$(web_key)"
    if [ -f "$crt" ] || [ -f "$key" ]; then
      if command -v openssl >/dev/null 2>&1 && [ -f "$crt" ] && [ -f "$key" ] && cert_is_selfsigned; then
        rm -f "$crt" "$key" 2>/dev/null || warn "删不掉 $crt / $key（权限？）：sudo rm -f 手工清。"
        rmdir "$WEB_CERT_DIR" 2>/dev/null || true
        log "→ 已删除自签证书（$WEB_CERT_DIR）"
      else
        warn "证书 $WEB_CERT_DIR 不是本脚本签的自签证书（或本机没有 openssl）：没删，需要清就手工处理。"
      fi
    fi
  fi

  if [ "$UNINSTALL_PURGE_BLOBS" = 1 ] && [ -n "${BLOB_PATH:-}" ] && [ -d "$BLOB_PATH" ]; then
    if rm -rf -- "$BLOB_PATH" 2>/dev/null; then
      log "→ 已删除宿主机附件目录 $BLOB_PATH"
    else
      warn "删不掉 $BLOB_PATH（权限？）：sudo rm -rf -- '$BLOB_PATH' 手工清。"
    fi
  fi

  if [ "$UNINSTALL_HAS_ENV" = 1 ] && [ "$EMBEDDED_PG" = 0 ] && [ "$UNINSTALL_DROP_EXTERNAL_DB" != 1 ]; then
    warn "外部库模式：$PG_HOST:$PG_PORT/$PG_DB 是别人的实例，本脚本没有 DROP。要连数据一起重来，加 --drop-external-db 重跑，或在那套 PG 上执行："
    warn "  DROP DATABASE \"$PG_DB\" WITH (FORCE);   -- 用 $PG_USER 或超级用户；宿主机上执行时把 host.docker.internal 换成本机地址"
  fi

  if [ "$UNINSTALL_KEEP_ENV" != 1 ] && [ -f "$ENV_FILE" ]; then
    rm -f "$ENV_FILE" || warn "删不掉 $ENV_FILE（权限？）：sudo rm -f 手工清。"
    log "→ 已删除 $ENV_FILE（下次 up 重新生成口令/密钥/证书）"
  fi

  log "✓ 卸载完成。重新部署：bash $SELF up"
}

# ── 入口 ────────────────────────────────────────────────────────────────
if [ "$ACTION" = info ]; then
  [ -f "$ENV_FILE" ] || die "还没有 $ENV_FILE，先跑 bash $SELF up（或 up --use-existing-pg \"...\"）。"
  env_normalize
  load_vars
  print_info
  exit 0
fi

if [ "$ACTION" = fix-pg-password ]; then
  [ -f "$ENV_FILE" ] || die "还没有 $ENV_FILE，看不出该把哪个口令对上：先跑 bash $SELF up（或 up --use-existing-pg \"...\"）。"
  env_normalize
  load_vars
  fix_pg_password
  exit 0
fi

# renew-cert 自己读 .env 并决定签不签：它只在证书快到期（或现场点名 --force）时动文件，
# systemd 定时器跑的就是这一条，所以这条路径必须能在没有任何 tty 的环境里跑完。
if [ "$ACTION" = renew-cert ]; then
  renew_cert
  exit 0
fi

case "$ACTION" in
  up|migrate) ensure_env ;;
  uninstall)
    # 卸载不需要 .env：半套部署、.env 被删过都可能发生，按项目标签照样能拆干净。
    # 有 .env 时读它，才能知道库是嵌入还是外部、附件绑在哪个目录、证书放哪。
    if [ -f "$ENV_FILE" ]; then
      UNINSTALL_HAS_ENV=1
      env_normalize
      load_vars
    else
      EMBEDDED_PG=1; WEB_ENABLED_ON=1; WEB_HTTPS_ON=1
      PG_HOST=""; PG_PORT=""; PG_DB=""; PG_USER=""; BLOB_PATH=""
      WEB_CERT_DIR="$DEFAULT_CERT_DIR"
      COMPOSE=(); COMPOSE_FILES=()
    fi ;;
  *)
    [ -f "$ENV_FILE" ] || die "还没有 $ENV_FILE，先跑 bash $SELF up。"
    env_normalize
    load_vars
    ;;
esac

case "$ACTION" in
  up)
    require_docker
    preflight_https
    preflight_ports
    prepare_blob_dir
    # 先把库起来并验一遍口令对得上，再花几十秒构建镜像：28P01 这类不一致与镜像无关，
    # 让它排在构建之前报出来（首装时构建才是有效工作）。
    prepare_db
    check_db_auth
    log "→ 构建 API 镜像"
    if [ "${SKIP_BUILD:-0}" = "1" ]; then
      log "   SKIP_BUILD=1，复用现有镜像"
    else
      compose build api
    fi
    if ! run_migrate; then migrate_failed_hint; die "数据库迁移失败，API 没有起。"; fi
    log "→ 起 API"
    compose up -d api
    wait_ready
    # 管理台排在 API 之后：nginx 起栈时要在容器网络里解析到 api 这个服务名，
    # 证书也只有在真要起 https 站点时才需要（纯 http 用不着 openssl）。
    if [ "$WEB_ENABLED_ON" = 1 ]; then
      if [ "$WEB_HTTPS_ON" = 1 ]; then ensure_web_cert; fi
      build_and_start_web
      install_cert_timer
    else
      remove_web_container
    fi
    compose ps
    print_info
    ;;
  migrate)
    require_docker
    preflight_ports
    prepare_blob_dir
    prepare_db
    check_db_auth
    [ "${SKIP_BUILD:-0}" = "1" ] || compose build api
    if ! run_migrate; then migrate_failed_hint; die "数据库迁移失败。"; fi
    log "✓ 迁移完成"
    ;;
  reset-admin-password)
    reset_admin_password
    ;;
  status)
    require_docker
    compose ps
    ;;
  logs)
    require_docker
    if [ "${#SERVICES[@]}" -gt 0 ]; then
      compose logs -f --tail 100 "${SERVICES[@]}"
    else
      compose logs -f --tail 100 api
    fi
    ;;
  health)
    curl -fsS "$base/healthz"; echo
    curl -fsS "$base/ready"; echo
    # 用 awk 而不是 head：head 提前退出会让 curl 收到 SIGPIPE，配合 pipefail 直接判失败
    if [ -n "$(env_value METRICS_SCRAPE_TOKEN)" ]; then
      curl -fsS -H "X-Metrics-Token: $(env_value METRICS_SCRAPE_TOKEN)" "$base/metrics" | awk 'NR<=20'
    else
      curl -fsS "$base/metrics" | awk 'NR<=20'
    fi
    # API 自己那三个端点好不代表管理台好：静态文件、反代、证书是三件独立会坏的事
    if [ "$WEB_ENABLED_ON" = 1 ]; then health_web; else log "（WEB_ENABLED=false：跳过管理台探测）"; fi
    ;;
  down)
    require_docker
    compose down
    if [ "$WEB_ENABLED_ON" != 1 ]; then remove_web_container; fi
    if [ "$EMBEDDED_PG" = 0 ]; then
      log "外部库模式：只停了 API 与管理台，$PG_HOST:$PG_PORT 上那套 PostgreSQL 不归本栈管，没动它。"
      leftover="$(docker ps -aq --filter name=appframework-platform-postgres 2>/dev/null || true)"
      if [ -n "$leftover" ]; then
        warn "还留着嵌入模式的数据库容器（$leftover）。要清掉它："
        warn "  docker compose $(compose_files_display) --env-file \"$ENV_FILE\" down"
      fi
    fi
    ;;
  uninstall)
    uninstall_stack
    ;;
  *)
    die "未知动作：$ACTION（可用 up|migrate|fix-pg-password|renew-cert|reset-admin-password|status|logs|health|down|uninstall|info）"
    ;;
esac
