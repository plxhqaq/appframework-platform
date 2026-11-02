#!/usr/bin/env bash
# 平台服务端一键起栈：检查依赖 → 生成 deploy/.env → 构建镜像 → 显式迁移 → 起服务 → 等就绪。
#
#   ./deploy/deploy.sh              # 等价于 up
#   ./deploy/deploy.sh up           # 首次会生成随机口令并建栈
#   ./deploy/deploy.sh migrate      # 只跑数据库迁移（升级镜像后先迁移再放量）
#   ./deploy/deploy.sh status|logs|health|down
#
# 刻意不做的两件事：不覆盖已存在的 .env（口令只生成一次）、不把口令写进任何日志以外的地方。
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_FILE="$ROOT_DIR/deploy/docker-compose.yml"
ENV_FILE="$ROOT_DIR/deploy/.env"
ENV_EXAMPLE="$ROOT_DIR/deploy/env.example"

log() { printf '%s\n' "$*"; }
die() { printf '× %s\n' "$*" >&2; exit 1; }

# ── 依赖检查 ────────────────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "没找到 docker 命令。"
if docker compose version >/dev/null 2>&1; then
  COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  die "需要 docker compose（v2）或 docker-compose。"
fi

compose() { "${COMPOSE[@]}" -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }

# ── .env 生成（只一次） ─────────────────────────────────────────────────
# 值全部用十六进制 + 固定前后缀：不含 # / 空格 / 引号，compose 解析不会踩坑；
# 管理员口令满足策略（8–128 且同时含字母与数字）。
ensure_env() {
  if [ -f "$ENV_FILE" ]; then
    log "使用已有 $ENV_FILE（不会覆盖）。"
    return
  fi
  [ -f "$ENV_EXAMPLE" ] || die "缺少 $ENV_EXAMPLE。"

  cp "$ENV_EXAMPLE" "$ENV_FILE"
  local pg_pass jwt_key admin_pass
  pg_pass="$(openssl rand -hex 24)"
  jwt_key="$(openssl rand -hex 32)"
  admin_pass="Adm-$(openssl rand -hex 12)-7"

  sed -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$pg_pass|" \
      -e "s|^JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$jwt_key|" \
      -e "s|^SEED_ADMIN_PASSWORD=.*|SEED_ADMIN_PASSWORD=$admin_pass|" \
      -i "$ENV_FILE"
  chmod 600 "$ENV_FILE" 2>/dev/null || true

  log "已生成 $ENV_FILE（权限 600，已被 .gitignore 忽略）："
  log "  · POSTGRES_PASSWORD / JWT_SIGNING_KEY 为随机值"
  log "  · 管理员引导口令 admin / $admin_pass"
  log "  · 首次登录会被强制改密，改完这个引导口令即失效"
  log "  · 授权签发私钥留空 = unmanaged；需要授权请填 LICENSING_SIGNING_KEY_PKCS8 后 ./deploy/deploy.sh up"
}

# 读 .env 里的一个键（缺失返回空，不触发 set -e）
env_value() { grep -E "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- | tr -d '[:space:]' || true; }

base_url() { printf 'http://localhost:%s' "${API_PORT:-8080}"; }

# ── 迁移：跑完即退的一次性容器 ──────────────────────────────────────────
# 生产不能用 AutoMigrate（多副本会互相抢），所以这里用镜像内同一入口加 --migrate。
run_migrate() {
  log "→ 数据库迁移（一次性容器）"
  compose run --rm --no-deps api --migrate
}

wait_migrate() {
  compose up -d postgres
  log "→ 等 PostgreSQL 就绪"
  local id attempt=0
  id="$(compose ps -q postgres)"
  while [ "$attempt" -lt 60 ]; do
    if [ "$(docker inspect --format '{{.State.Health.Status}}' "$id" 2>/dev/null || echo starting)" = "healthy" ]; then
      return
    fi
    attempt=$((attempt + 1))
    sleep 2
  done
  die "PostgreSQL 60 次探测内没就绪，看 ./deploy/deploy.sh logs postgres"
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

summary() {
  local metrics_note="（匿名可读，公网可达时请在 .env 设 METRICS_SCRAPE_TOKEN）"
  if [ -n "$(env_value METRICS_SCRAPE_TOKEN)" ]; then
    metrics_note="（需带 X-Metrics-Token）"
  fi

  log ""
  log "栈已起来："
  log "  API      $(base_url)/api/v1"
  log "  存活     $(base_url)/healthz      就绪 $(base_url)/ready"
  log "  指标     $(base_url)/metrics $metrics_note"
  log "  接口文档 $(base_url)/scalar/v1  原始文档 $(base_url)/openapi/v1.json"
  log "  管理台   本机跑 cd web && pnpm install && pnpm dev（/api 代理到 $(base_url)）"
  log ""
  log "验证链路（服务端在跑就能跑通）："
  log "  PLATFORM_URL=$(base_url) PLATFORM_PASSWORD='<.env 里的 SEED_ADMIN_PASSWORD>' \\"
  log "    dotnet run --project samples/ConsoleQuickstart -c Release"
  log ""
  log "下一步：改 .env 里的对外端口与 ASPNETCORE_ENVIRONMENT，把 deploy/.env 交给密钥管理系统。"
}

# ── 入口 ────────────────────────────────────────────────────────────────
ACTION="${1:-up}"
case "$ACTION" in
  up|migrate) ensure_env ;;
esac

API_PORT="$(env_value API_PORT)"
API_PORT="${API_PORT:-8080}"
base="$(base_url)"

case "$ACTION" in
  up)
    docker info >/dev/null 2>&1 || die "docker 守护进程没起来（Docker Desktop / systemctl start docker）。"
    wait_migrate
    run_migrate
    log "→ 构建并起 API"
    if [ "${SKIP_BUILD:-0}" = "1" ]; then
      compose up -d api
    else
      compose up -d --build api
    fi
    wait_ready
    compose ps
    summary
    ;;
  migrate)
    docker info >/dev/null 2>&1 || die "docker 守护进程没起来。"
    wait_migrate
    run_migrate
    ;;
  status)
    compose ps
    ;;
  logs)
    shift
    if [ "$#" -gt 0 ]; then
      compose logs -f --tail 100 "$@"
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
    ;;
  down)
    compose down
    ;;
  *)
    die "未知动作：$ACTION（可用 up|migrate|status|logs|health|down）"
    ;;
esac
