# syntax=docker/dockerfile:1
# Adelaide.Web production image. Multi-stage:
#   1) Angular client (Node)  -> dist/adelaide-web-client/browser
#   2) .NET publish (SDK)     -> framework-dependent linux-x64 (the shape the linux-native-smoke proves loads)
#   3) runtime (aspnet:10.0)  -> the EXACT base CI's native smoke validates (libSkiaSharp / libpdfium / libe_sqlite3)
# Build context = repo root. .dockerignore keeps .env, PHI (samples/*.pdf/inbox/*.db/sidecars), node_modules, bin/obj,
# and infra state OUT of the build context/image.

# ---- 1) Angular client ----
FROM node:22-bookworm-slim AS client
WORKDIR /client
COPY src/Adelaide.Web.Client/package.json src/Adelaide.Web.Client/package-lock.json ./
RUN npm ci
COPY src/Adelaide.Web.Client/ ./
RUN npm run build   # @angular/build:application, production by default -> dist/adelaide-web-client/browser

# ---- 2) .NET publish ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore as its own cached layer: the shared props + every csproj in the web app's project graph, then restore.
COPY Directory.Build.props ./
COPY src/Adelaide.Core/Adelaide.Core.csproj             src/Adelaide.Core/
COPY src/Adelaide.Demo/Adelaide.Demo.csproj             src/Adelaide.Demo/
COPY src/Adelaide.Ai/Adelaide.Ai.csproj                 src/Adelaide.Ai/
COPY src/Adelaide.SharePoint/Adelaide.SharePoint.csproj src/Adelaide.SharePoint/
COPY src/Adelaide.Audit/Adelaide.Audit.csproj           src/Adelaide.Audit/
COPY src/Adelaide.Web/Adelaide.Web.csproj               src/Adelaide.Web/
RUN dotnet restore src/Adelaide.Web/Adelaide.Web.csproj -r linux-x64
# Sources + publish: RID-specific, framework-dependent -> flattens the linux natives into the output so they resolve on
# aspnet:10.0 (the exact shape the native smoke verifies). Directory.Build.props keeps warnings non-fatal.
COPY src/ ./src/
RUN dotnet publish src/Adelaide.Web/Adelaide.Web.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app/publish

# ---- 3) runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
# The built Angular SPA, served by the app itself (Program.cs reads ADELAIDE_CLIENT_DIST and falls back to index.html).
COPY --from=client /client/dist/adelaide-web-client/browser ./client
# ADELAIDE_CONTAINER is the durability guard's explicit marker: inside this image a capture store that resolves to a
# local FILE PATH is a fatal startup error, because container disk does not survive a restart and capture writes fail
# open (the loss would be silent). See TranscriptRetentionGate.DurabilityError / docs/CAPTURE.md.
ENV ADELAIDE_CLIENT_DIST=/app/client \
    ASPNETCORE_HTTP_PORTS=8080 \
    ADELAIDE_CONTAINER=1
EXPOSE 8080
# FINDING 1c: run as the non-root user the base image defines ($APP_UID / "app"), so a compromise of the native
# rendering path (PDFium/Skia on untrusted PDFs) does NOT already hold root inside the container. The app binaries stay
# root-owned (read-only to the app user); only the DATA dirs are handed to $APP_UID so the app can still write captured
# PDFs / crash-safe sidecars (inbox) and logs. In Azure the inbox is the /mnt/inbox Azure Files mount (ADELAIDE_INBOX),
# which App Service mounts world-writable (file/dir_mode 0777) → $APP_UID can write there too; the audit store is Azure
# SQL (no local DB file). VERIFY the container can write /mnt/inbox after deploy (see the remediation summary). Port
# 8080 (> 1024) needs no privilege to bind.
RUN mkdir -p /app/inbox /app/logs && chown -R $APP_UID /app/inbox /app/logs
USER $APP_UID
ENTRYPOINT ["dotnet", "Adelaide.Web.dll"]
