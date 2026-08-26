# Внутренний C#-движок исполнения действий. Для этого сервиса не публикуется ни один host-порт —
# снаружи Docker Compose до него достучаться невозможно.

# Стадия сборки: полный SDK .NET, нужен только на этапе compose build, в конечный образ не попадает.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала копируются только файлы проектов (.csproj) — это позволяет Docker закешировать шаг
# dotnet restore и не выполнять его заново при каждой правке .cs-файлов, если сами зависимости
# проекта не менялись.
COPY src/Directory.Build.props src/
COPY src/Course.Shared/Course.Shared.csproj src/Course.Shared/
COPY src/Course.Api/Course.Api.csproj src/Course.Api/
RUN dotnet restore src/Course.Api/Course.Api.csproj

COPY contracts/ contracts/
COPY src/ src/
RUN dotnet publish src/Course.Api/Course.Api.csproj -c Release -o /app/publish --no-restore

# Стадия выполнения: только облегчённый образ ASP.NET Core runtime (без SDK), меньше по размеру
# и без лишних инструментов сборки в конечном контейнере.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
# Запуск от имени непривилегированного пользователя образа (не root) — стандартная практика
# уменьшения поверхности атаки контейнера.
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/Course.Api.dll"]
