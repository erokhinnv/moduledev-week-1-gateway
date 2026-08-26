#!/bin/sh
# Инициализация кластера, выполняется РОВНО ОДИН РАЗ официальным entrypoint-скриптом образа
# postgres, и только на пустом (только что созданном) томе данных.
#
# Создаёт три прикладные роли модели доверия проекта:
#   course_owner    - владелец (NOLOGIN, без права входа) каждой схемы, таблицы и функции
#                      SECURITY DEFINER. Сам по себе он никогда не логинится — он лишь "держатель"
#                      прав владения, а сессии от его имени выполняются CLI через SET ROLE.
#   course_migrator - роль публикации, используется ТОЛЬКО сервисом cli (миграции, каталог действий)
#   course_runtime  - runtime-роль, используется ТОЛЬКО сервисом api (обработка HTTP-запросов)
set -e

psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" \
     --dbname "$POSTGRES_DB" \
     -v dbname="$POSTGRES_DB" \
     -v migrator_password="$COURSE_MIGRATOR_PASSWORD" \
     -v runtime_password="$COURSE_RUNTIME_PASSWORD" <<'EOSQL'
CREATE ROLE course_owner NOLOGIN;
CREATE ROLE course_migrator LOGIN PASSWORD :'migrator_password' IN ROLE course_owner;
CREATE ROLE course_runtime LOGIN PASSWORD :'runtime_password';

REVOKE ALL ON DATABASE :"dbname" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"dbname" TO course_owner, course_migrator, course_runtime;
GRANT CREATE ON DATABASE :"dbname" TO course_owner;

ALTER SCHEMA public OWNER TO course_owner;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Зарегистрированные предметные функции никогда не должны быть доступны напрямую: только
-- api.invoke получает явный GRANT EXECUTE (см. миграцию 006_autocheck.sql). Эти две строки
-- меняют права ПО УМОЛЧАНИЮ для будущих объектов, создаваемых от имени course_owner и
-- course_migrator, — чтобы забыть выдать явный GRANT было безопаснее, чем забыть его отозвать.
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_migrator REVOKE ALL ON TABLES FROM PUBLIC;
EOSQL
