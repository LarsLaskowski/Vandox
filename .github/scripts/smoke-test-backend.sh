#!/usr/bin/env bash
# Smoke test of the backend container started from deploy/backend/docker-compose.yml (record 0060).
# Usage: smoke-test-backend.sh <image>   (the image must exist locally, for example vandox:local)
#
# Checks the health check, the login (the hash comes from the image's own hash-password), the web UI's static files and security policy, the port bindings, the resource limits, the owner and mode of /data in the image
# (65532:65532, 0700; /data is the only entry the image adds that is owned by the container user, apart from
# the base image's /home/nonroot), the graceful stop and that the database survives re-creating the container.
# Reads no repository secret, pushes nothing and uses no network beyond
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
[[ -n "$dir" ]] && [[ -d "$dir" ]] || fail "could not create the project directory"

cid=""

cleanup() {
  # Remove the containers and the volume first: the files they use are removed afterwards.
  if [[ -n "$cid" ]]; then
    docker rm -f "$cid" >/dev/null 2>&1 || true
  fi
  (cd "$dir" && docker compose -p "$project" down -v --remove-orphans) >/dev/null 2>&1 || true
  if [[ -n "$dir" ]] && [[ -d "$dir" ]]; then
    sudo rm -rf -- "$dir"
  fi
}
trap cleanup EXIT

cp "$repo/deploy/backend/docker-compose.yml" "$repo/deploy/backend/vandoxd.yaml" "$dir/"
mkdir "$dir/secrets" "$dir/import"
openssl rand -hex 32 >"$dir/secrets/vandox_agent_token"
sudo chown 65532:65532 "$dir/secrets/vandox_agent_token"
sudo chmod 0400 "$dir/secrets/vandox_agent_token"
# The web password: the hash is created by the image itself, as the README tells the operator.
password="$(openssl rand -hex 16)"
printf '%s\n' "$password" | docker run --rm -i "$image" hash-password >"$dir/secrets/vandox_web_password_hash"
[[ -s "$dir/secrets/vandox_web_password_hash" ]] || fail "hash-password printed no hash"
sudo chown 65532:65532 "$dir/secrets/vandox_web_password_hash"
sudo chmod 0400 "$dir/secrets/vandox_web_password_hash"
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
  [[ -n "$id" ]] || fail "no container for the vandoxd service"
  for _ in $(seq 1 60); do
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id")"
    [[ "$status" = "healthy" ]] && return 0
    sleep 1
  done
  docker logs "$id" >&2 || true
  fail "container is '$status' after 60 s, want healthy"
}

inspect() {
  local format="$1"
  docker inspect --format "$format" "$(container_id)"
}

compose config --quiet || fail "docker compose config failed"

compose up -d
wait_healthy

[[ "$(curl -fsS http://127.0.0.1:8080/healthz)" = "ok" ]] || fail "/healthz on the web port did not answer ok"

# Reads the policy from the ContentSecurityPolicy constant of the middleware, so the script keeps no copy of it.
# The constant must be declared on exactly one line.
read_policy() {
  local file="$repo/src/Vandox.Backend/Hosting/SecurityHeadersMiddleware.cs"
  local pattern='public const string ContentSecurityPolicy = "([^"]*)";'
  local -a lines
  mapfile -t lines < <(grep -F 'public const string ContentSecurityPolicy = "' "$file" | tr -d '\r')
  [[ "${#lines[@]}" -eq 1 ]] || fail "found ${#lines[@]} declarations of ContentSecurityPolicy in $file, want exactly 1"
  [[ "${lines[0]}" =~ $pattern ]] || fail "could not read the policy from: ${lines[0]}"
  printf '%s' "${BASH_REMATCH[1]}"
}

# Checks what the unit tests cannot see because they run the build output: the published image serves the web
# UI's static files and the login page carries exactly one Content-Security-Policy header with the documented value.
check_web_ui() {
  local want="$1"
  local path code headers got
  local -a found
  for path in /_framework/blazor.web.js /app.css /lib/uplot/uPlot.esm.js; do
    code="$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:8080$path")"
    [[ "$code" == "200" ]] || fail "GET $path on the web port answered $code, want 200"
  done
  headers="$(curl -fsS -D - -o /dev/null http://127.0.0.1:8080/login | tr -d '\r')"
  mapfile -t found < <(grep -i '^content-security-policy:' <<<"$headers")
  [[ "${#found[@]}" -eq 1 ]] || fail "GET /login carries ${#found[@]} Content-Security-Policy headers, want exactly 1"
  got="${found[0]#*:}"
  got="${got# }"
  [[ "$got" == "$want" ]] || fail "Content-Security-Policy is '$got', want '$want'"
}

# Checks the login: an anonymous page load is sent to the login page, the login form posts with its antiforgery
# token, and the session cookie it sets opens the start page.
check_login() {
  local pass="$1"
  local jar="$dir/cookies.txt"
  local base="http://127.0.0.1:8080"
  local code redirect html token
  local base_url="$base/"
  read -r code redirect < <(curl -sS -o /dev/null -w '%{http_code} %{redirect_url}\n' "$base_url")
  [[ "$code" == "302" && "$redirect" == */login ]] || fail "anonymous GET / answered $code ($redirect), want 302 to /login"
  html="$(curl -fsS -c "$jar" "$base/login")"
  token="$(grep -oE 'name="__RequestVerificationToken"[^>]*value="[^"]*"' <<<"$html" | head -n 1 | sed -E 's/.*value="([^"]*)"/\1/')"
  [[ -n "$token" ]] || fail "the login page has no antiforgery token"
  code="$(curl -sS -b "$jar" -c "$jar" -o /dev/null -w '%{http_code}' --data-urlencode "__RequestVerificationToken=$token" --data-urlencode "_handler=login" --data-urlencode "password=$pass" "$base/login")"
  [[ "$code" == "302" ]] || fail "POST /login with the right password answered $code, want 302"
  code="$(curl -sS -b "$jar" -o /dev/null -w '%{http_code}' "$base_url")"
  [[ "$code" == "200" ]] || fail "GET / with the session answered $code, want 200"
}

policy="$(read_policy)"
[[ -n "$policy" ]] || fail "the policy read from the middleware is empty"
check_web_ui "$policy"
check_login "$password"

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

[[ "$(inspect '{{.HostConfig.Memory}}')" = "536870912" ]] || fail "memory limit is not 536870912"
[[ "$(inspect '{{.HostConfig.RestartPolicy.Name}}')" = "unless-stopped" ]] || fail "restart policy is not unless-stopped"
[[ "$(inspect '{{.HostConfig.ReadonlyRootfs}}')" = "true" ]] || fail "root file system is not read-only"
[[ "$(inspect '{{.Config.User}}')" = "65532:65532" ]] || fail "container user is not 65532:65532"

# Checks a numeric `tar -tv` listing of the image: the data/ entry must be exactly drwx------ 65532/65532
# and no other entry may be owned (uid or gid) by 65532, except home/nonroot/ and what is below it.
# The owner is matched on its own column, so a file name containing 65532 cannot match.
check_image_owners() {
  local listing="$1"
  local mode owner name uid gid path
  local dataentry="" offenders=""
  while read -r mode owner _ _ _ name; do
    [[ -n "$mode" ]] || continue
    path="${name%% -> *}"
    path="${path%% link to *}"
    path="${path#./}"
    uid="${owner%%/*}"
    gid="${owner##*/}"
    if [[ "$path" == "data/" ]]; then
      dataentry="$mode $owner"
    fi
    if [[ "$uid" != "65532" && "$gid" != "65532" ]]; then
      continue
    fi
    case "$path" in
      data/ | home/nonroot/ | home/nonroot/*) ;;
      *) offenders+="$mode $owner $path"$'\n' ;;
    esac
  done <<<"$listing"
  if [[ "$dataentry" != "drwx------ 65532/65532" ]]; then
    fail "/data in the image is '$dataentry', want drwx------ 65532/65532"
  fi
  if [[ -n "$offenders" ]]; then
    fail "image entries owned by 65532 besides /data and /home/nonroot:"$'\n'"$offenders"
  fi
}

# Only /data may be owned by the container user (AC-C1): it must be private, and the image adds no other
# entry owned by 65532 (the base image's /home/nonroot is allowed).
cid="$(docker create "$image")"
[[ -n "$cid" ]] || fail "could not create a container from the image"
listing="$(docker export "$cid" | tar --numeric-owner -tvf -)"
[[ -n "$listing" ]] || fail "the exported image file system listing is empty"
check_image_owners "$listing"

first="$(container_id)"
compose stop
[[ "$(docker inspect --format '{{.State.ExitCode}}' "$first")" = "0" ]] || fail "container did not exit with code 0 on stop"
logs="$(docker logs "$first" 2>&1)"
grep -q 'vandoxd stopped' <<<"$logs" || fail "log has no 'vandoxd stopped' line"

# Re-create the container; the named volume stays (no -v).
compose down
compose up -d
second="$(container_id)"
[[ -n "$second" ]] && [[ "$second" != "$first" ]] || fail "container was not re-created"
wait_healthy
logs="$(docker logs "$second" 2>&1)"
grep -q '"created":false' <<<"$logs" || fail "database was not reopened after re-creation (no \"created\":false)"

echo "backend container smoke test passed"
