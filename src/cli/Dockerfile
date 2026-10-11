# Build context: repository root (docker build -f src/cli/Dockerfile .)
# The cmdb CLI for integrations (#217): one CronJob per adapter runs `cmdb sync <adapter>` with the integration's own
# service account. Non-root and any UID, as OpenShift assigns.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/cli/Cmdb.Cli.csproj src/cli/
RUN dotnet restore src/cli/Cmdb.Cli.csproj
COPY src/cli/ src/cli/
RUN dotnet publish src/cli/Cmdb.Cli.csproj -c Release -o /app --no-restore -p:PublishSingleFile=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build --chown=0:0 --chmod=g=u /app .
ENV DOTNET_EnableDiagnostics=0 \
    HOME=/tmp
USER $APP_UID
ENTRYPOINT ["dotnet", "cmdb.dll"]
