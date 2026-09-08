# ── Frontend ──────────────────────────────────────────────────────────────────
# The repo's layout is mirrored rather than flattened, because vite writes its
# build to ../api/wwwroot: the API project serves those files, so the output
# belongs beside the API rather than inside the frontend.
FROM node:22-alpine AS frontend

# No TTY in a build, and pnpm asks before replacing a modules directory.
ENV CI=true

RUN corepack enable

WORKDIR /src

# The manifests alone first, so the install layer survives an ordinary UI change.
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./frontend/
RUN cd frontend && pnpm install --frozen-lockfile

COPY frontend/ ./frontend/
RUN cd frontend && pnpm build

# ── API ───────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api

WORKDIR /src

# global.json first: it pins the SDK feature band, and without it here a restore
# could run on a different one than the repository asks for.
COPY global.json ./
COPY api/StruxRelay.csproj ./api/
RUN dotnet restore api/StruxRelay.csproj

COPY api/ ./api/
COPY --from=frontend /src/api/wwwroot ./api/wwwroot
RUN dotnet publish api/StruxRelay.csproj -c Release -o /app --no-restore

# ── Runtime ───────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# curl is here only for the healthcheck below. The aspnet image ships neither it
# nor wget, and the stdlib-Python trick the old image used went with the Python.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=api /app ./

# The database goes on the mounted volume, so which devices are approved survives
# a restart or an image update. Double underscore is how a nested configuration
# key is spelled in an environment variable.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Relay__Database__ConnectionString="Data Source=/app/data/relay.sqlite"

# Non-root, and the user is created explicitly rather than borrowing the base
# image's: the relay binds 8080 (unprivileged) and writes nothing outside
# /app/data.
RUN useradd --system --create-home --uid 10001 relay \
 && mkdir -p /app/data \
 && chown relay:relay /app/data
USER relay

EXPOSE 8080

# Liveness only — a connected device is not a health condition.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "StruxRelay.dll"]
