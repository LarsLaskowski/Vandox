# Backend image for vandoxd. The build context is the repository root:
#   docker build -f deploy/backend/Dockerfile .
# Base images are pinned by digest only; each tag is kept in its own ARG for readability and the
# OCI base-image labels. The builder tag follows the `go` directive in go.mod. The release workflow
# checks both. The digests are refreshed by hand in a pull request; a weekly workflow and the CI
# release build check report a stale digest (docs/CONTRIBUTING.md, records 0041 and 0055).
# The HEALTHCHECK runs `vandoxd -healthcheck` (record 0059); the image declares no EXPOSE on purpose.
ARG BASE_BUILD_IMAGE="golang"
ARG BASE_BUILD_TAG="1.27-trixie"
ARG BASE_BUILD_DIGEST="sha256:3b77fc618ec235a1ab412de7737f120dd507c57e8d87de4cbb7994fb94275ed5"
ARG BASE_RUNTIME_IMAGE="gcr.io/distroless/static-debian13"
ARG BASE_RUNTIME_TAG="nonroot"
ARG BASE_RUNTIME_DIGEST="sha256:e2e927ec666bae08560abb3c55d0659eceabb657f56b6782ab500a9fc7f555e3"

FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build

WORKDIR /src

# The builder's Go version must match BASE_BUILD_TAG, so a digest read for another Go version fails
# the build (record 0055). Runs before go.mod is copied, so no toolchain switch can answer instead.
ARG BASE_BUILD_TAG
RUN want="go${BASE_BUILD_TAG%%-*}"; got="$(go env GOVERSION)"; \
    case "$got" in "$want"|"$want".*) ;; \
      *) echo "builder Go $got does not match BASE_BUILD_TAG $BASE_BUILD_TAG (want $want)" >&2; exit 1 ;; \
    esac

COPY go.mod go.sum ./
RUN go mod download

COPY cmd/ cmd/
COPY internal/ internal/

ARG VERSION=dev
ARG COMMIT=unknown
ARG DATE=unknown

RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -buildvcs=false \
    -ldflags "-s -w \
      -X github.com/LarsLaskowski/Vandox/internal/version.Version=${VERSION} \
      -X github.com/LarsLaskowski/Vandox/internal/version.Commit=${COMMIT} \
      -X github.com/LarsLaskowski/Vandox/internal/version.Date=${DATE}" \
    -o /out/vandoxd ./cmd/vandoxd

# Empty data directory, copied into the runtime image as the seed of the data volume (record 0059).
RUN mkdir -p -m 0700 /out/data

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

COPY --from=build /out/vandoxd /vandoxd

# The only resource owned by the non-root user: one empty directory that is the writable seed of the data
# volume (docker:S6504 is accepted, record 0059). A new named volume copies its ownership, and the
# distroless runtime has no shell for a `RUN chown`; the binary and everything else stay root-owned.
COPY --from=build --chown=65532:65532 /out/data /data

USER 65532:65532

# No EXPOSE: `docker run -P` and NAS container managers would publish every exposed port on all
# interfaces. The ports are bound in deploy/backend/docker-compose.yml (record 0059).
STOPSIGNAL SIGTERM

# The binary is the probe: the distroless image has no shell or curl. No arguments means: serve.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --start-interval=2s --retries=3 \
    CMD ["/vandoxd", "-healthcheck"]

ENTRYPOINT ["/vandoxd"]
