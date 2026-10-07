# Backend image for vandoxd (.NET 10, Blazor). The build context is the repository root:
#   docker build -f deploy/backend/Dockerfile .
# Base images are pinned by digest only; each tag is kept in its own ARG for readability and the
# OCI base-image labels. The builder tag follows the target framework in Directory.Build.props. The release
# workflow checks both. The digests are refreshed by hand in a pull request; a weekly workflow and the CI
# release build check report a stale digest (docs/CONTRIBUTING.md, records 0041 and 0080).
# The HEALTHCHECK runs `vandoxd -healthcheck` (record 0059); the image declares no EXPOSE on purpose.
ARG BASE_BUILD_IMAGE="mcr.microsoft.com/dotnet/sdk"
ARG BASE_BUILD_TAG="10.0"
ARG BASE_BUILD_DIGEST="sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317"
ARG BASE_RUNTIME_IMAGE="mcr.microsoft.com/dotnet/aspnet"
ARG BASE_RUNTIME_TAG="10.0-noble-chiseled"
ARG BASE_RUNTIME_DIGEST="sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c"

FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build

WORKDIR /src

# Restore first, so the layer is reused while only code changes.
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props Vandox.slnx ./
COPY src/Vandox.Core/Vandox.Core.csproj src/Vandox.Core/
COPY src/Vandox.Storage/Vandox.Storage.csproj src/Vandox.Storage/
COPY src/Vandox.Import/Vandox.Import.csproj src/Vandox.Import/
COPY src/Vandox.Backend/Vandox.Backend.csproj src/Vandox.Backend/
RUN dotnet restore src/Vandox.Backend/Vandox.Backend.csproj

COPY src/ src/

ARG VERSION=dev
ARG COMMIT=unknown
ARG DATE=unknown

# Framework-dependent publish onto the chiseled ASP.NET runtime: no apphost, no debug symbols. The build arguments are
# unset for MSBuild, which would otherwise read VERSION as its own Version property and reject a tag such as v1.2.3.
RUN env -u VERSION -u COMMIT -u DATE \
      dotnet publish src/Vandox.Backend/Vandox.Backend.csproj -c Release --no-restore -o /out \
      -p:UseAppHost=false -p:DebugType=none \
      -p:VandoxVersion="${VERSION}" -p:VandoxCommit="${COMMIT}" -p:VandoxDate="${DATE}"

# Empty data directory, staged as /rootfs/data and copied into the runtime image as the seed of the data
# volume (record 0059). It is staged inside a root tree so that /data is itself a copied entry, which keeps
# its mode 0700; COPY of a directory only copies its contents into a new destination directory with 0755.
RUN mkdir -p /rootfs && mkdir -m 0700 /rootfs/data

FROM ${BASE_RUNTIME_IMAGE}@${BASE_RUNTIME_DIGEST}

ARG BASE_RUNTIME_IMAGE
ARG BASE_RUNTIME_TAG
ARG BASE_RUNTIME_DIGEST
ARG VERSION=dev
ARG COMMIT=unknown
ARG DATE=unknown

LABEL org.opencontainers.image.title="vandox" \
      org.opencontainers.image.description="Vandox backend: lean monitoring for a Plesk-managed Linux server that reconstructs outages from logs and metrics" \
      org.opencontainers.image.source="https://github.com/LarsLaskowski/Vandox" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}" \
      org.opencontainers.image.created="${DATE}" \
      org.opencontainers.image.base.name="${BASE_RUNTIME_IMAGE}:${BASE_RUNTIME_TAG}" \
      org.opencontainers.image.base.digest="${BASE_RUNTIME_DIGEST}"

WORKDIR /app
COPY --from=build /out/ /app/

# The only resource this image adds that is owned by the non-root user: one empty directory that is the
# writable seed of the data volume (docker:S6504 is accepted, record 0059). A new named volume copies its
# ownership, and the chiseled runtime has no shell for a `RUN chown`; the application files stay root-owned.
# The source is a root tree holding only data/: the contents are merged into the existing `/`, whose
# owner and mode stay untouched, while data/ keeps its mode 0700 and gets the owner 65532:65532.
COPY --from=build --chown=65532:65532 /rootfs/ /

USER 65532:65532

# The configuration comes from the YAML file and the environment, never from ASPNETCORE_URLS (the listeners
# are the two sockets of the configuration); no diagnostics pipe, no telemetry, no first-run work.
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_URLS=

# No EXPOSE: `docker run -P` and NAS container managers would publish every exposed port on all
# interfaces. The ports are bound in deploy/backend/docker-compose.yml (record 0059).
STOPSIGNAL SIGTERM

# The application is the probe: the chiseled image has no shell or curl. No arguments means: serve.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --start-interval=2s --retries=3 \
    CMD ["dotnet", "/app/vandoxd.dll", "-healthcheck"]

ENTRYPOINT ["dotnet", "/app/vandoxd.dll"]
