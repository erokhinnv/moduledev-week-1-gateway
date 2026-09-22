#!/usr/bin/env bash
# Прогон интеграционных тестов против живого контура.
#
# Юнит-тесты (src/Course.Tests) не требуют ничего, кроме .NET, и выполняются обычным
# "dotnet test". Интеграционные же требуют поднятого Docker Compose, поэтому по умолчанию
# помечаются пропущенными — этот скрипт поднимает контур, выполняет их и гасит контур за собой.
set -euo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

project="${COURSE_INTEGRATION_COMPOSE_PROJECT:-moduledev-week-1-itest}"
export COURSE_INTEGRATION_COMPOSE_PROJECT="$project"
export COURSE_INTEGRATION_BASE_URL="${COURSE_INTEGRATION_BASE_URL:-http://localhost:8080}"

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
