# API image. Build from the repo root:  docker build -t telemetry-triage-api .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until a project file changes.
COPY global.json Directory.Build.props ./
COPY src/TelemetryTriage.Core/TelemetryTriage.Core.csproj src/TelemetryTriage.Core/
COPY src/TelemetryTriage.Infrastructure/TelemetryTriage.Infrastructure.csproj src/TelemetryTriage.Infrastructure/
COPY src/TelemetryTriage.Api/TelemetryTriage.Api.csproj src/TelemetryTriage.Api/
RUN dotnet restore src/TelemetryTriage.Api/TelemetryTriage.Api.csproj

COPY src/ src/
COPY runbooks/ runbooks/
COPY evals/ evals/
RUN dotnet publish src/TelemetryTriage.Api/TelemetryTriage.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Non-root user provided by the .NET base images.
USER $APP_UID
ENTRYPOINT ["dotnet", "TelemetryTriage.Api.dll"]
