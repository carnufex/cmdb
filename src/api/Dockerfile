# Build context: repository root (docker build -f src/api/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/api/Cmdb.Api.csproj src/api/
COPY src/database/Cmdb.Database.csproj src/database/
COPY src/catalog/Cmdb.Catalog.csproj src/catalog/
COPY src/graph/Cmdb.Graph.csproj src/graph/
RUN dotnet restore src/api/Cmdb.Api.csproj
COPY catalog/ catalog/
COPY docs/ docs/
COPY src/database/ src/database/
COPY src/catalog/ src/catalog/
COPY src/graph/ src/graph/
COPY src/api/ src/api/
RUN dotnet publish src/api/Cmdb.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Owned by root group and group-readable so an arbitrary UID in group 0 (OpenShift) can run it.
COPY --from=build --chown=0:0 --chmod=g=u /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Cmdb.Api.dll"]
