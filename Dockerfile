# ── Stage 1: Build Next.js frontend ─────────────────────────────────────────
FROM node:22-slim AS frontend-build
WORKDIR /frontend

COPY src/Themearr.Web/package.json src/Themearr.Web/package-lock.json* ./
RUN npm ci

COPY src/Themearr.Web/ .
RUN npm run build
# Output is in /frontend/out (static export)

# ── Stage 2: Build .NET API ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS api-build
WORKDIR /src

COPY src/Themearr.API/ ./
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 3: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# Install yt-dlp, ffmpeg
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl python3 \
    && rm -rf /var/lib/apt/lists/*

RUN curl -sSL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    -o /usr/local/bin/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp

WORKDIR /app

# Copy .NET publish output
COPY --from=api-build /app/publish ./

# Copy Next.js static export into wwwroot (served by .NET)
COPY --from=frontend-build /frontend/out ./wwwroot/

# Non-root user — the service does not need root inside the container.
RUN groupadd -r themearr && useradd -r -g themearr -d /opt/themearr -s /sbin/nologin themearr \
    && mkdir -p /opt/themearr/data \
    && chown -R themearr:themearr /app /opt/themearr \
    && chmod 700 /opt/themearr/data

USER themearr

ARG APP_VERSION=dev
ENV APP_VERSION=${APP_VERSION}
# Bind to loopback by default. docker-compose must re-publish only to 127.0.0.1
# on the host — remote exposure requires a reverse proxy with its own auth/TLS.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

ENTRYPOINT ["dotnet", "Themearr.API.dll"]
