# Backend image for vandoxd. The build context is the repository root:
#   docker build -f deploy/backend/Dockerfile .
# The builder tag follows the `go` directive in go.mod (checked by the release workflow).
FROM golang:1.24-trixie@sha256:5835f052b784aa39f2fe9070def3568605c8bc3fcd810f10402066348b61e716 AS build

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

FROM gcr.io/distroless/static-debian13:nonroot@sha256:e2e927ec666bae08560abb3c55d0659eceabb657f56b6782ab500a9fc7f555e3

ARG VERSION=dev
ARG COMMIT=unknown
ARG DATE=unknown

LABEL org.opencontainers.image.title="vandox" \
      org.opencontainers.image.source="https://github.com/LarsLaskowski/Vandox" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}" \
      org.opencontainers.image.created="${DATE}"

COPY --from=build /out/vandoxd /vandoxd

USER 65532:65532

ENTRYPOINT ["/vandoxd"]
