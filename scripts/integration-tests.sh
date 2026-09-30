#!/usr/bin/env bash
# Прогон интеграционных и end-to-end тестов против живого контура.
#
# Юнит-тесты (src/Course.Tests, integration/tests) не требуют ничего, кроме .NET и Python, и
# выполняются обычными "dotnet test" и "python3 -m unittest". Интеграционные же требуют поднятого
# Docker Compose, поэтому по умолчанию помечаются пропущенными — этот скрипт поднимает контур,
# выполняет их вместе с e2e-сценариями Python-периметра и гасит контур за собой.
set -euo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

# У JWT, HMAC, capability и токенов нет значений по умолчанию в compose.yaml: если окружение не
# задано снаружи, генерируются одноразовые synthetic-значения только для этого прогона.
if [[ -z "${COURSE_JWT_SIGNING_KEY:-}" ]]; then
  eval "$(python3 scripts/dev_env.py)"
fi

project="${COURSE_INTEGRATION_COMPOSE_PROJECT:-moduledev-week-4-itest}"
export COURSE_INTEGRATION_COMPOSE_PROJECT="$project"
export COURSE_GATEWAY_PORT="${COURSE_GATEWAY_PORT:-8080}"
export COURSE_INTEGRATION_BASE_URL="${COURSE_INTEGRATION_BASE_URL:-http://localhost:$COURSE_GATEWAY_PORT}"
export COMPOSE_PROJECT="$project"

cleanup() {
  docker compose --project-name "$project" down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "поднимаю контур (project: $project)"
docker compose --project-name "$project" up -d --build

echo "жду готовности $COURSE_INTEGRATION_BASE_URL/health/ready"
for _ in $(seq 1 60); do
  if curl -fsS "$COURSE_INTEGRATION_BASE_URL/health/ready" >/dev/null 2>&1; then
    break
  fi
  sleep 2
done

echo "выполняю интеграционные тесты"
dotnet test src/Course.IntegrationTests/Course.IntegrationTests.csproj -c Release --nologo

echo "выполняю e2e-сценарии Python-периметра"
python3 scripts/e2e_perimeter.py

echo "выполняю e2e-сценарии ядра недель 1–2"
python3 scripts/e2e_core.py
