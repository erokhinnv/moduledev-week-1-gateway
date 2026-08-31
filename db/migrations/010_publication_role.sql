-- 010_publication_role.sql
-- Права отдельной роли публикации course_publisher.
--
-- Роль создаётся в db/init/00-roles.sh, то есть однократно при инициализации ПУСТОГО тома. На
-- базе, развёрнутой до появления этой миграции, роли не будет: выполнить CREATE ROLE отсюда
-- нельзя — у course_owner, от имени которого идут миграции, нет атрибута CREATEROLE, и выдавать
-- его владельцу ради одной операции значило бы расширить именно ту роль, которую мы стараемся
-- держать узкой. Поэтому гранты обёрнуты в проверку существования роли: на новом томе миграция
-- выдаёт права, на старом — тихо пропускает их, не падая.
--
-- Для уже развёрнутой базы разделение включается разово, вручную, от суперпользователя:
--   CREATE ROLE course_publisher LOGIN PASSWORD '...';
--   GRANT CONNECT ON DATABASE course TO course_publisher;
-- после чего повторный migration apply не нужен — достаточно выполнить этот же файл заново
-- (он идемпотентен) или перезапустить bootstrap.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'course_publisher') THEN
        RAISE NOTICE 'course_publisher role is absent, publication grants skipped';
        RETURN;
    END IF;

    -- Доступ к схеме, где живут функции управления каталогом.
    EXECUTE 'GRANT USAGE ON SCHEMA api TO course_publisher';

    -- Ровно пять функций управления каталогом и ничего больше. В частности, api.invoke роли
    -- публикации НЕ выдаётся: она управляет описанием действий, но не может исполнить ни одно
    -- из них. Прямых прав на таблицы у неё тоже нет — всё идёт через SECURITY DEFINER функции,
    -- которые выполняются от имени course_owner.
    EXECUTE 'GRANT EXECUTE ON FUNCTION api.publish_action(jsonb) TO course_publisher';
    EXECUTE 'GRANT EXECUTE ON FUNCTION api.activate_action(text, text, integer) TO course_publisher';
    EXECUTE 'GRANT EXECUTE ON FUNCTION api.disable_action(text, text, integer, integer) TO course_publisher';
    EXECUTE 'GRANT EXECUTE ON FUNCTION api.list_actions() TO course_publisher';
    EXECUTE 'GRANT EXECUTE ON FUNCTION api.resolve_action(text, text, integer) TO course_publisher';
END;
$$;

-- Отзывать те же гранты у course_migrator бессмысленно и было бы самообманом: он член
-- course_owner, то есть владельца этих функций, и выполнить их сможет в любом случае. Разделение
-- достигается не отзывом прав у мигратора, а тем, что рутинная публикация больше НЕ ТРЕБУЕТ его
-- учётных данных — команды action * ходят под course_publisher, а course_migrator нужен только
-- команде migration apply.
