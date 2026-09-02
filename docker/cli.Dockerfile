# Course CLI. Владеет учётными данными миграций и публикации — сервис api их никогда не получает.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Directory.Build.props src/
COPY src/Course.Shared/Course.Shared.csproj src/Course.Shared/
COPY src/Course.Cli/Course.Cli.csproj src/Course.Cli/
# contracts/ копируется уже на этом шаге (раньше, чем для api и gateway), потому что CLI встраивает
# JSON Schema манифеста внутрь своей сборки как embedded resource (см. Course.Cli.csproj) —
# файл нужен уже на этапе restore/build, а не только на этапе publish.
COPY contracts/ contracts/
RUN dotnet restore src/Course.Cli/Course.Cli.csproj

COPY src/ src/
RUN dotnet publish src/Course.Cli/Course.Cli.csproj -c Release -o /app/publish --no-restore

# Обратите внимание: конечный образ CLI основан на "dotnet/runtime", а не на "dotnet/aspnet" —
# у CLI нет HTTP-сервера, поэтому лишние ASP.NET Core компоненты ему не нужны.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
# Миграции и встроенные манифесты действий кладутся прямо в образ — команда "bootstrap"
# применяет их при первом старте контейнера, без обращения к внешним источникам.
COPY db/migrations/ /app/db/migrations/
COPY db/actions/ /app/db/actions/
COPY db/flows/ /app/db/flows/
ENV DOTNET_EnableDiagnostics=0 \
    COURSE_BOOTSTRAP_MIGRATIONS=/app/db/migrations \
    COURSE_BOOTSTRAP_ACTIONS=/app/db/actions \
    COURSE_BOOTSTRAP_FLOWS=/app/db/flows
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/course.dll"]
# Команда по умолчанию при "docker compose up": накатить миграции и опубликовать встроенные
# действия. Другие команды (validate/publish/activate/...) передаются явно через
# "docker compose run cli <команда>", подменяя это значение по умолчанию.
CMD ["bootstrap"]
