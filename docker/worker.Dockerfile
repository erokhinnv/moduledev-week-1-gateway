# Сменный исполнитель процессов. Один и тот же образ запускается двумя сервисами Compose
# (worker-a и worker-b) — они различаются только идентификатором арендатора в переменной
# окружения, и ничем больше. Именно поэтому «worker можно заменить»: в образе нет ни одной
# строки, привязанной к конкретной карте, шагу или действию.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только файлы проектов — чтобы Docker закешировал restore отдельно от правок кода.
COPY src/Directory.Build.props src/
COPY src/Course.Shared/Course.Shared.csproj src/Course.Shared/
COPY src/Course.Workflow.Worker/Course.Workflow.Worker.csproj src/Course.Workflow.Worker/
# contracts/ нужен уже на этапе restore: схемы контракта встраиваются в сборку как embedded resource.
COPY contracts/ contracts/
RUN dotnet restore src/Course.Workflow.Worker/Course.Workflow.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Course.Workflow.Worker/Course.Workflow.Worker.csproj -c Release -o /app/publish --no-restore

# У worker нет HTTP-стека приложения: три служебных адреса наблюдаемости обслуживает обычный
# сокет (ObservabilityEndpoint), поэтому конечный образ основан на dotnet/runtime, а не aspnet.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV DOTNET_EnableDiagnostics=0
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/Workflow.Worker.dll"]
