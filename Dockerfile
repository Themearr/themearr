# ── Stage 1: Build Vite/React frontend ─────────────────────────────────────────
# Pin build stages to the native BUILDPLATFORM: the outputs (static SPA bundle +
# portable .NET IL) are architecture-independent, so multi-arch images build
# fast without emulating npm/dotnet under QEMU for arm64.
FROM --platform=$BUILDPLATFORM node:22-slim AS frontend-build
WORKDIR /frontend

COPY src/Themearr.Web/package.json src/Themearr.Web/package-lock.json* ./
RUN npm ci

COPY src/Themearr.Web/ .
RUN npm run build
# Output is in /frontend/out (static SPA bundle)

# ── Stage 2: Build .NET API ───────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS api-build
WORKDIR /src

COPY src/Themearr.API/ ./
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 3: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# No extra system packages: theme audio is fetched over HTTP from the
# youtube-mp36 RapidAPI (see DownloadService), so yt-dlp/ffmpeg are not needed.

WORKDIR /app

# Copy .NET publish output
COPY --from=api-build /app/publish ./

# Copy the static SPA bundle into wwwroot (served by .NET, with SPA fallback)
COPY --from=frontend-build /frontend/out ./wwwroot/

# Non-root user — the service does not need root inside the container.
RUN groupadd -r themearr && useradd -r -g themearr -d /opt/themearr -s /sbin/nologin themearr \
    && mkdir -p /opt/themearr/data \
    && chown -R themearr:themearr /app /opt/themearr \
    && chmod 700 /opt/themearr/data

USER themearr

ARG APP_VERSION=dev
ENV APP_VERSION=${APP_VERSION}
# Bind to all interfaces INSIDE the container — this is required, since Docker's
# port publishing can't reach a container that only listens on its own loopback.
# Host exposure is restricted separately by docker-compose publishing to
# 127.0.0.1:8080 only; remote access needs a reverse proxy with its own auth/TLS.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

ENTRYPOINT ["dotnet", "Themearr.API.dll"]
