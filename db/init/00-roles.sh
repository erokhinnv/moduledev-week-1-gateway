#!/bin/sh
# Инициализация кластера, выполняется РОВНО ОДИН РАЗ официальным entrypoint-скриптом образа
# postgres, и только на пустом (только что созданном) томе данных.
#
# Создаёт прикладные роли модели доверия проекта:
#   course_owner     - владелец (NOLOGIN, без права входа) каждой схемы, таблицы и функции
#                      SECURITY DEFINER. Сам по себе он никогда не логинится — он лишь "держатель"
#                      прав владения, а сессии от его имени выполняются CLI через SET ROLE.
#   course_migrator  - роль МИГРАЦИЙ: меняет структуру базы. Член course_owner, потому что DDL
#                      требует прав владельца. Используется только командой migration apply.
#   course_publisher - роль ПУБЛИКАЦИИ: управляет каталогом действий и больше ничем. НЕ член
#                      course_owner, поэтому не может ни выполнить DDL, ни тронуть предметные
#                      таблицы, ни вызвать api.invoke. Используется командами action *.
#   course_runtime   - runtime-роль, используется ТОЛЬКО сервисом api (обработка HTTP-запросов).
#   course_operator  - роль ОПЕРАЦИЙ над процессами: запуск, сигналы, чтение и trusted-проба
#                      fencing. Не член course_owner и не имеет прав публикации, поэтому её
#                      учётные данные не открывают ни DDL, ни каталог действий, ни карты.
#   workflow_worker  - роль сменного исполнителя процессов (сервисы worker-a и worker-b). Ей
#                      выдаются EXECUTE ровно на четыре функции-границы (claim_jobs, api.invoke,
#                      finish_job, fail_job) и НИ ОДНОГО права на таблицы: см. 015_workflow_autocheck.
#                      Она не член course_owner, поэтому ничего не наследует.
#   outbox_dispatcher - роль Python-диспетчера Outbox (неделя 3). EXECUTE ровно на три функции
#                      delivery.claim_outbox / succeed_outbox / fail_outbox, ни одной таблицы.
#   inbox_reconciler - роль Python-согласователя Inbox (неделя 3). EXECUTE ровно на одну функцию
#                      delivery.reconcile_inbox. Обе роли не состоят ни в одной другой роли.
#   autocheck_reader - роль наблюдателя (неделя 4). Здесь у неё только CONNECT, а USAGE схемы
#                      autocheck и SELECT её представлений выдаёт миграция 024_reliability.sql.
#                      Ни членства, ни прав на физические отношения, функции, последовательности,
#                      CREATE и TEMP у неё нет. Пароль приходит только из окружения запуска
#                      (COURSE_AUTOCHECK_PASSWORD, значения по умолчанию в compose.yaml нет): без
#                      него роль создаётся без пароля и войти под ней нельзя.
#
# Здесь же закрепляется профиль доставки и аренды. Значения приходят в сервис postgres
# переменными окружения и становятся настройками САМОЙ базы (ALTER DATABASE ... SET):
# политику повторов определяет авторитетное состояние, а не клиент, вызывающий границу.
#
# Почему миграции и публикация разведены. Раньше обе задачи выполняла одна роль course_migrator,
# а она — член course_owner с атрибутом INHERIT, то есть обладает правами владельца НЕЯВНО, даже
# без SET ROLE. Значит, учётные данные, нужные всего лишь для публикации манифеста, открывали
# заодно полный DDL и прямой доступ к предметному состоянию. Теперь публикацию можно доверить
# отдельной стороне (например сборочному конвейеру), не выдавая ей ничего сверх каталога.
set -e

# Числовые настройки профиля проверяются до ALTER DATABASE: пустое, нулевое или нечисловое
# значение обязано остановить инициализацию, а не молча стать частью политики доставки.
require_positive_integer() {
    case "$2" in
        ''|*[!0-9]*|0) echo "$1 must be a positive integer" >&2; exit 1;;
    esac
}

# Отсутствие jitter — осмысленная настройка (один dispatcher, предсказуемое расписание), поэтому
# ноль здесь допустим, в отличие от числа попыток или срока аренды.
require_non_negative_integer() {
    case "$2" in
        ''|*[!0-9]*) echo "$1 must be a non-negative integer" >&2; exit 1;;
    esac
}

: "${COURSE_OUTBOX_MAX_ATTEMPTS:=5}"
: "${COURSE_OUTBOX_BACKOFF_BASE_MS:=1000}"
: "${COURSE_OUTBOX_BACKOFF_MAX_MS:=60000}"
: "${COURSE_OUTBOX_JITTER_MAX_MS:=0}"
: "${COURSE_OUTBOX_LEASE_MS:=30000}"
: "${COURSE_JOB_LEASE_MS:=30000}"

require_positive_integer COURSE_OUTBOX_MAX_ATTEMPTS "$COURSE_OUTBOX_MAX_ATTEMPTS"
require_positive_integer COURSE_OUTBOX_BACKOFF_BASE_MS "$COURSE_OUTBOX_BACKOFF_BASE_MS"
require_positive_integer COURSE_OUTBOX_BACKOFF_MAX_MS "$COURSE_OUTBOX_BACKOFF_MAX_MS"
require_non_negative_integer COURSE_OUTBOX_JITTER_MAX_MS "$COURSE_OUTBOX_JITTER_MAX_MS"
require_positive_integer COURSE_OUTBOX_LEASE_MS "$COURSE_OUTBOX_LEASE_MS"
require_positive_integer COURSE_JOB_LEASE_MS "$COURSE_JOB_LEASE_MS"

psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" \
     --dbname "$POSTGRES_DB" \
     -v dbname="$POSTGRES_DB" \
     -v migrator_password="$COURSE_MIGRATOR_PASSWORD" \
     -v publisher_password="$COURSE_PUBLISHER_PASSWORD" \
     -v runtime_password="$COURSE_RUNTIME_PASSWORD" \
     -v worker_password="$COURSE_WORKER_PASSWORD" \
     -v operator_password="$COURSE_OPERATOR_PASSWORD" \
     -v outbox_password="$COURSE_OUTBOX_PASSWORD" \
     -v inbox_password="$COURSE_INBOX_PASSWORD" \
     -v autocheck_password="$COURSE_AUTOCHECK_PASSWORD" \
     -v outbox_max_attempts="$COURSE_OUTBOX_MAX_ATTEMPTS" \
     -v outbox_backoff_base_ms="$COURSE_OUTBOX_BACKOFF_BASE_MS" \
     -v outbox_backoff_max_ms="$COURSE_OUTBOX_BACKOFF_MAX_MS" \
     -v outbox_jitter_max_ms="$COURSE_OUTBOX_JITTER_MAX_MS" \
     -v outbox_lease_ms="$COURSE_OUTBOX_LEASE_MS" \
     -v job_lease_ms="$COURSE_JOB_LEASE_MS" <<'EOSQL'
CREATE ROLE course_owner NOLOGIN;
CREATE ROLE course_migrator LOGIN PASSWORD :'migrator_password' IN ROLE course_owner;
CREATE ROLE course_publisher LOGIN PASSWORD :'publisher_password';
CREATE ROLE course_runtime LOGIN PASSWORD :'runtime_password';
CREATE ROLE course_operator LOGIN PASSWORD :'operator_password';
CREATE ROLE workflow_worker LOGIN PASSWORD :'worker_password';
CREATE ROLE outbox_dispatcher LOGIN PASSWORD :'outbox_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE inbox_reconciler LOGIN PASSWORD :'inbox_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE autocheck_reader LOGIN PASSWORD :'autocheck_password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

REVOKE ALL ON DATABASE :"dbname" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"dbname" TO course_owner, course_migrator, course_publisher, course_runtime, course_operator, workflow_worker, outbox_dispatcher, inbox_reconciler, autocheck_reader;
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

-- Профиль доставки и аренды как настройки базы. Границы delivery.* читают их из каталога
-- (pg_db_role_setting), а не из сессии, и не принимают аргументом: клиент границы не может
-- подменить профиль ни параметром вызова, ни собственным SET. Срок аренды задания — значение по
-- умолчанию для workflow.claim_jobs, когда исполнитель не передал свой p_lease_ms.
ALTER DATABASE :"dbname" SET course.outbox_max_attempts    = :'outbox_max_attempts';
ALTER DATABASE :"dbname" SET course.outbox_backoff_base_ms = :'outbox_backoff_base_ms';
ALTER DATABASE :"dbname" SET course.outbox_backoff_max_ms  = :'outbox_backoff_max_ms';
ALTER DATABASE :"dbname" SET course.outbox_jitter_max_ms   = :'outbox_jitter_max_ms';
ALTER DATABASE :"dbname" SET course.outbox_lease_ms        = :'outbox_lease_ms';
ALTER DATABASE :"dbname" SET course.job_lease_ms           = :'job_lease_ms';
EOSQL
