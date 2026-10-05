#!/usr/bin/env bash
# Smoke test of the backend container started from deploy/backend/docker-compose.yml (record 0060).
# Usage: smoke-test-backend.sh <image>   (the image must exist locally, for example vandox:local)
#
# Checks the health check, the port bindings, the resource limits, the owner and mode of /data in the image
# (65532:65532, 0700), the graceful stop and that the database survives re-creating the container. Reads no repository secret, pushes nothing and uses no network beyond
# loopback. sudo is used only to give the generated token file to the container user (as the README tells
# the operator) and to remove the temporary project directory it owns.
set -euo pipefail

image="${1:?usage: smoke-test-backend.sh <image>}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

fail() {
  echo "::error::$*" >&2
  exit 1
}

project="vandoxsmoke$$"
dir="$(mktemp -d)"
[ -n "$dir" ] && [ -d "$dir" ] || fail "could not create the project directory"

cid=""

cleanup() {
  # Remove the containers and the volume first: the files they use are removed afterwards.
  if [ -n "$cid" ]; then
    docker rm -f "$cid" >/dev/null 2>&1 || true
  fi
  (cd "$dir" && docker compose -p "$project" down -v --remove-orphans) >/dev/null 2>&1 || true
  if [ -n "$dir" ] && [ -d "$dir" ]; then
    sudo rm -rf -- "$dir"
  fi
}
trap cleanup EXIT

cp "$repo/deploy/backend/docker-compose.yml" "$repo/deploy/backend/vandoxd.yaml" "$dir/"
mkdir "$dir/secrets" "$dir/import"
openssl rand -hex 32 >"$dir/secrets/vandox_agent_token"
sudo chown 65532:65532 "$dir/secrets/vandox_agent_token"
sudo chmod 0400 "$dir/secrets/vandox_agent_token"
cat >"$dir/docker-compose.override.yml" <<OVERRIDE
services:
  vandoxd:
    image: ${image}
    pull_policy: never
OVERRIDE

export WEB_BIND_ADDRESS=127.0.0.1
compose() { (cd "$dir" && docker compose -p "$project" "$@"); }

# Prints the container ID of the service.
container_id() { compose ps -q vandoxd; }

# Waits until the container reports healthy, for at most 60 seconds.
wait_healthy() {
  local id status
  id="$(container_id)"
  [ -n "$id" ] || fail "no container for the vandoxd service"
  for _ in $(seq 1 60); do
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id")"
    [ "$status" = "healthy" ] && return 0
    sleep 1
  done
  docker logs "$id" >&2 || true
  fail "container is '$status' after 60 s, want healthy"
}

inspect() { docker inspect --format "$1" "$(container_id)"; }

compose config --quiet || fail "docker compose config failed"

compose up -d
wait_healthy

[ "$(curl -fsS http://127.0.0.1:8080/healthz)" = "ok" ] || fail "/healthz on the web port did not answer ok"

ingest="$(inspect '{{json (index .NetworkSettings.Ports "8081/tcp")}}')"
case "$ingest" in
  null | "[]" | "") ;;
  *) fail "ingest port 8081 has a host binding: $ingest" ;;
esac

exposed="$(docker image inspect --format '{{json .Config.ExposedPorts}}' "$image")"
case "$exposed" in
  null | "{}") ;;
  *) fail "image declares exposed ports: $exposed, want none" ;;
esac

[ "$(inspect '{{.HostConfig.Memory}}')" = "536870912" ] || fail "memory limit is not 536870912"
[ "$(inspect '{{.HostConfig.RestartPolicy.Name}}')" = "unless-stopped" ] || fail "restart policy is not unless-stopped"
[ "$(inspect '{{.HostConfig.ReadonlyRootfs}}')" = "true" ] || fail "root file system is not read-only"
[ "$(inspect '{{.Config.User}}')" = "65532:65532" ] || fail "container user is not 65532:65532"

# The /data directory baked into the image must belong to the container user and be private (AC-C1).
cid="$(docker create "$image")"
[ -n "$cid" ] || fail "could not create a container from the image"
listing="$(docker export "$cid" | tar --numeric-owner -tvf - data/)"
dataentry="$(grep -E ' data/$' <<<"$listing" || true)"
case "$dataentry" in
  "drwx------ 65532/65532"*) ;;
  *) fail "/data in the image is '$dataentry', want drwx------ 65532/65532" ;;
esac

first="$(container_id)"
compose stop
[ "$(docker inspect --format '{{.State.ExitCode}}' "$first")" = "0" ] || fail "container did not exit with code 0 on stop"
logs="$(docker logs "$first" 2>&1)"
grep -q 'vandoxd stopped' <<<"$logs" || fail "log has no 'vandoxd stopped' line"

# Re-create the container; the named volume stays (no -v).
compose down
compose up -d
second="$(container_id)"
[ -n "$second" ] && [ "$second" != "$first" ] || fail "container was not re-created"
wait_healthy
logs="$(docker logs "$second" 2>&1)"
grep -q '"created":false' <<<"$logs" || fail "database was not reopened after re-creation (no \"created\":false)"

echo "backend container smoke test passed"
