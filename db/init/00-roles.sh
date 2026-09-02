#!/bin/sh
# Инициализация кластера, выполняется РОВНО ОДИН РАЗ официальным entrypoint-скриптом образа
# postgres, и только на пустом (только что созданном) томе данных.
#
# Создаёт четыре прикладные роли модели доверия проекта:
#   course_owner     - владелец (NOLOGIN, без права входа) каждой схемы, таблицы и функции
#                      SECURITY DEFINER. Сам по себе он никогда не логинится — он лишь "держатель"
#                      прав владения, а сессии от его имени выполняются CLI через SET ROLE.
#   course_migrator  - роль МИГРАЦИЙ: меняет структуру базы. Член course_owner, потому что DDL
#                      требует прав владельца. Используется только командой migration apply.
#   course_publisher - роль ПУБЛИКАЦИИ: управляет каталогом действий и больше ничем. НЕ член
#                      course_owner, поэтому не может ни выполнить DDL, ни тронуть предметные
#                      таблицы, ни вызвать api.invoke. Используется командами action *.
#   course_runtime   - runtime-роль, используется ТОЛЬКО сервисом api (обработка HTTP-запросов).
#   workflow_worker  - роль сменного исполнителя процессов (сервисы worker-a и worker-b). Ей
#                      выдаются EXECUTE ровно на четыре функции-границы (claim_jobs, api.invoke,
#                      finish_job, fail_job) и НИ ОДНОГО права на таблицы: см. 015_workflow_autocheck.
#                      Она не член course_owner, поэтому ничего не наследует.
#
# Почему миграции и публикация разведены. Раньше обе задачи выполняла одна роль course_migrator,
# а она — член course_owner с атрибутом INHERIT, то есть обладает правами владельца НЕЯВНО, даже
# без SET ROLE. Значит, учётные данные, нужные всего лишь для публикации манифеста, открывали
# заодно полный DDL и прямой доступ к предметному состоянию. Теперь публикацию можно доверить
# отдельной стороне (например сборочному конвейеру), не выдавая ей ничего сверх каталога.
set -e

psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" \
     --dbname "$POSTGRES_DB" \
     -v dbname="$POSTGRES_DB" \
     -v migrator_password="$COURSE_MIGRATOR_PASSWORD" \
     -v publisher_password="$COURSE_PUBLISHER_PASSWORD" \
     -v runtime_password="$COURSE_RUNTIME_PASSWORD" \
     -v worker_password="$COURSE_WORKER_PASSWORD" <<'EOSQL'
CREATE ROLE course_owner NOLOGIN;
CREATE ROLE course_migrator LOGIN PASSWORD :'migrator_password' IN ROLE course_owner;
CREATE ROLE course_publisher LOGIN PASSWORD :'publisher_password';
CREATE ROLE course_runtime LOGIN PASSWORD :'runtime_password';
CREATE ROLE workflow_worker LOGIN PASSWORD :'worker_password';

REVOKE ALL ON DATABASE :"dbname" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"dbname" TO course_owner, course_migrator, course_publisher, course_runtime, workflow_worker;
GRANT CREATE ON DATABASE :"dbname" TO course_owner;

ALTER SCHEMA public OWNER TO course_owner;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Зарегистрированные предметные функции никогда не должны быть доступны напрямую: только
-- api.invoke получает явный GRANT EXECUTE (см. миграцию 006_autocheck.sql). Эти строки меняют
-- права ПО УМОЛЧАНИЮ для будущих объектов, создаваемых от имени course_owner и course_migrator, —
-- чтобы забыть выдать явный GRANT было безопаснее, чем забыть его отозвать.
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_migrator REVOKE ALL ON TABLES FROM PUBLIC;
EOSQL
