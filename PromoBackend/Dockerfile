# syntax=docker/dockerfile:1

# =============================================================================
# PromoEngine API - production image
#
# Build context: PromoBackend/
#   docker build -f PromoBackend/Dockerfile PromoBackend
#
# Stage 1 restores and publishes with the .NET SDK; stage 2 carries only the
# published output on the much smaller ASP.NET runtime image. No credentials are
# baked in at any layer - every secret arrives as an environment variable at run
# time (see docker-compose.yml).
# =============================================================================

# ---------- stage 1: restore ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Project files are copied on their own first so that `dotnet restore` lands in a
# layer that is only invalidated when a dependency actually changes. Editing C#
# then rebuilds from the cached restore instead of re-downloading every package.
COPY PromoEngine.slnx ./
COPY src/PromoEngine.Domain/PromoEngine.Domain.csproj                 src/PromoEngine.Domain/
COPY src/PromoEngine.Infrastructure/PromoEngine.Infrastructure.csproj src/PromoEngine.Infrastructure/
COPY src/PromoEngine.Api/PromoEngine.Api.csproj                       src/PromoEngine.Api/

# Restoring into the image layer rather than a BuildKit cache mount is
# deliberate. A cache mount is not part of the layer, so on a fresh runner that
# restores this layer from the registry cache the mount would be empty and the
# --no-restore publish below would fail on missing packages. The layer cache
# alone gives the same speed-up and is reproducible.
RUN dotnet restore src/PromoEngine.Api/PromoEngine.Api.csproj

# ---------- stage 2: build and publish ----------
FROM restore AS publish
COPY src/ src/

RUN dotnet publish src/PromoEngine.Api/PromoEngine.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        /p:UseAppHost=false

# ---------- stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# curl is the container's own health probe. It is the only addition to the base
# runtime image.
RUN apt-get update \
 && apt-get install --yes --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=publish /app/publish ./

# Kestrel listens on 8080 so the process can run unprivileged; binding 80 would
# require root. Nothing outside the Docker network reaches this port directly.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1

EXPOSE 8080

# $APP_UID is the non-root account (uid 1654) that ships with the ASP.NET base
# image. A compromised process here has no root in the container.
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "PromoEngine.Api.dll"]
