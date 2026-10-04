# Backend image for vandoxd. The build context is the repository root:
#   docker build -f deploy/backend/Dockerfile .
# Base images are pinned by digest only; each tag is kept in its own ARG for readability and the
# OCI base-image labels. The builder tag follows the `go` directive in go.mod. The release workflow
# checks both; the digests are refreshed by hand (docs/CONTRIBUTING.md, record 0041).
ARG BASE_BUILD_IMAGE="golang"
ARG BASE_BUILD_TAG="1.27-trixie"
ARG BASE_BUILD_DIGEST="sha256:3b77fc618ec235a1ab412de7737f120dd507c57e8d87de4cbb7994fb94275ed5"
ARG BASE_RUNTIME_IMAGE="gcr.io/distroless/static-debian13"
ARG BASE_RUNTIME_TAG="nonroot"
ARG BASE_RUNTIME_DIGEST="sha256:e2e927ec666bae08560abb3c55d0659eceabb657f56b6782ab500a9fc7f555e3"

FROM ${BASE_BUILD_IMAGE}@${BASE_BUILD_DIGEST} AS build

WORKDIR /src

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

FROM ${BASE_RUNTIME_IMAGE}@${BASE_RUNTIME_DIGEST}

ARG BASE_RUNTIME_IMAGE
ARG BASE_RUNTIME_TAG
ARG BASE_RUNTIME_DIGEST
ARG VERSION=dev
ARG COMMIT=unknown
ARG DATE=unknown

LABEL org.opencontainers.image.title="vandox" \
      org.opencontainers.image.source="https://github.com/LarsLaskowski/Vandox" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}" \
      org.opencontainers.image.created="${DATE}" \
      org.opencontainers.image.base.name="${BASE_RUNTIME_IMAGE}:${BASE_RUNTIME_TAG}" \
      org.opencontainers.image.base.digest="${BASE_RUNTIME_DIGEST}"

COPY --from=build /out/vandoxd /vandoxd

USER 65532:65532

ENTRYPOINT ["/vandoxd"]
