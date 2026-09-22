#!/usr/bin/env bash
# Публичная обёртка над сервисом course cli. Сама автопроверка (check.sh) вызывает сервис cli
# напрямую через docker compose run, минуя этот скрипт — он существует для удобства ручного
# использования (например "./course.sh action list").
set -euo pipefail
exec docker compose run --rm -T cli "$@"
