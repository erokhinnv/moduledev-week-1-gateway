# Публичный C# ASP.NET Core шлюз. Единственный сервис во всём стеке, который публикует host-порт.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем только файлы проектов первыми — чтобы Docker мог закешировать dotnet restore
# отдельно от изменений в исходном коде.
COPY src/Directory.Build.props src/
COPY src/Course.Shared/Course.Shared.csproj src/Course.Shared/
COPY src/Course.Gateway/Course.Gateway.csproj src/Course.Gateway/
RUN dotnet restore src/Course.Gateway/Course.Gateway.csproj

COPY contracts/ contracts/
COPY src/ src/
RUN dotnet publish src/Course.Gateway/Course.Gateway.csproj -c Release -o /app/publish --no-restore

# Облегчённый образ только с ASP.NET Core runtime — без SDK и инструментов сборки.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/Course.Gateway.dll"]
