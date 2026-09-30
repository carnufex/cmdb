# Build context: repository root (docker build -f src/datagen/Dockerfile .)
# Runs as a one-off Job in the demo cluster, see scripts/load-demo-data.sh.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/datagen/Cmdb.DataGen.csproj src/datagen/
COPY src/database/Cmdb.Database.csproj src/database/
COPY src/catalog/Cmdb.Catalog.csproj src/catalog/
COPY src/graph/Cmdb.Graph.csproj src/graph/
RUN dotnet restore src/datagen/Cmdb.DataGen.csproj
COPY catalog/ catalog/
COPY changelog/ changelog/
COPY src/database/ src/database/
COPY src/catalog/ src/catalog/
COPY src/graph/ src/graph/
COPY src/datagen/ src/datagen/
RUN dotnet publish src/datagen/Cmdb.DataGen.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build --chown=0:0 --chmod=g=u /app .
ENV DOTNET_EnableDiagnostics=0
USER $APP_UID
ENTRYPOINT ["dotnet", "Cmdb.DataGen.dll"]
