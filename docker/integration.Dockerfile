# Python-периметр: ОДИН локально собранный образ для трёх сервисов Compose. Сервисы различаются
# только аргументом entrypoint и учётными данными окружения — ни одна строка образа не привязана
# к конкретному процессу, карте или действию.
#
# Главный процесс контейнера — сам интерпретатор Python (без shell-обёртки): так он получает
# SIGTERM напрямую и корректно останавливается по docker compose stop.
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

WORKDIR /app

COPY integration/requirements.txt ./requirements.txt
RUN python -m pip install --no-compile -r requirements.txt

# Только исходники пакета: тесты, секреты и .env в образ не попадают.
COPY integration/course_integration/ ./course_integration/

RUN useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin integration
USER 10001

ENTRYPOINT ["python", "-m", "course_integration"]
