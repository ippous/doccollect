# Image unique (API + client Blazor WebAssembly), utilisée pour dev, test et prod.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restauration en couche séparée pour profiter du cache Docker.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/DocCollect.Shared/DocCollect.Shared.csproj src/DocCollect.Shared/
COPY src/DocCollect.Domain/DocCollect.Domain.csproj src/DocCollect.Domain/
COPY src/DocCollect.Core/DocCollect.Core.csproj src/DocCollect.Core/
COPY src/DocCollect.Infrastructure/DocCollect.Infrastructure.csproj src/DocCollect.Infrastructure/
COPY src/DocCollect.Client/DocCollect.Client.csproj src/DocCollect.Client/
COPY src/DocCollect.Api/DocCollect.Api.csproj src/DocCollect.Api/
RUN dotnet restore src/DocCollect.Api/DocCollect.Api.csproj

COPY src/ src/
ARG VERSION=0.0.0-local
RUN dotnet publish src/DocCollect.Api/DocCollect.Api.csproj -c Release -o /app --no-restore /p:Version=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Storage__FallbackLocalPath=/data/storage
RUN mkdir -p /data/storage && chown -R $APP_UID /data
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
# Pas de HEALTHCHECK : l'image aspnet ne contient ni curl ni wget. La sonde /healthz est utilisée par le déploiement (CI).
ENTRYPOINT ["dotnet", "DocCollect.Api.dll"]
