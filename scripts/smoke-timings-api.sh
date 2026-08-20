#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
api_url=
log_path=$(mktemp)
upstream_log_path=$(mktemp)
slow_status_path=$(mktemp)
untrusted_log_path=$(mktemp)
api_pid=
upstream_pid=
slow_pid=

cleanup() {
  if [[ -n "$api_pid" ]]; then
    kill "$api_pid" 2>/dev/null || true
    wait "$api_pid" 2>/dev/null || true
  fi
  if [[ -n "$upstream_pid" ]]; then
    kill "$upstream_pid" 2>/dev/null || true
    wait "$upstream_pid" 2>/dev/null || true
  fi
  if [[ -n "$slow_pid" ]]; then
    kill "$slow_pid" 2>/dev/null || true
    wait "$slow_pid" 2>/dev/null || true
  fi
  rm -f "$log_path" "$upstream_log_path" "$slow_status_path" "$untrusted_log_path"
}
trap cleanup EXIT

python3 "$repo_root/scripts/stub-timings-upstream.py" >"$upstream_log_path" 2>&1 &
upstream_pid=$!
for _ in {1..120}; do
  upstream_url=$(sed -n '1p' "$upstream_log_path")
  if [[ "$upstream_url" == http://127.0.0.1:* ]]; then
    break
  fi
  if ! kill -0 "$upstream_pid" 2>/dev/null; then
    cat "$upstream_log_path" >&2
    exit 1
  fi
  sleep 0.25
done
[[ "$upstream_url" == http://127.0.0.1:* ]]

CENSORPLAYER_TRUSTED_PROXIES=127.0.0.1 \
CENSORPLAYER_RTE_BASE_URL="$upstream_url/api" \
ASPNETCORE_URLS=http://127.0.0.1:0 \
  dotnet run \
    --project "$repo_root/src/Censor.Timings.Api/Censor.Timings.Api.csproj" \
    --configuration Release \
    --no-restore >"$log_path" 2>&1 &
api_pid=$!

for _ in {1..120}; do
  api_url=$(sed -n 's/^[[:space:]]*Now listening on: //p' "$log_path" | tail -n 1)
  if [[ -n "$api_url" ]]; then
    health=$(curl -fsS "$api_url/healthz" 2>/dev/null || true)
    if [[ "$health" == *'"status":"ok"'* ]]; then
      break
    fi
  fi
  if ! kill -0 "$api_pid" 2>/dev/null; then
    cat "$log_path" >&2
    exit 1
  fi
  sleep 0.25
done

health=$(curl -fsS "$api_url/healthz")
[[ "$health" == *'"status":"ok"'* ]]
# Each logical section uses its own trusted-proxy partition, so adding an assertion
# cannot accidentally exhaust another section's 30-request budget.
status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --header 'X-Forwarded-For: 203.0.113.1' \
  "$api_url/v1/movies/search?q=")
[[ "$status" == 400 ]]

private_query=private-query-accepted
status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --header 'X-Forwarded-For: 203.0.113.1' \
  --get --data-urlencode "q=$private_query" \
  "$api_url/v1/movies/search")
[[ "$status" == 200 ]]
status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --header 'X-Forwarded-For: 203.0.113.12' \
  "$api_url/v1/movies/search?q=private-query-failed")
[[ "$status" == 502 ]]

search_payload=$(curl -fsS \
  --header 'X-Forwarded-For: 203.0.113.1' \
  "$api_url/v1/movies/search?q=aggregate-success")
printf '%s' "$search_payload" |
  jq -e '.schemaVersion == 1 and .movies[0].id == "301"' >/dev/null
timings_payload=$(curl -fsS \
  --header 'X-Forwarded-For: 203.0.113.1' \
  "$api_url/v1/movies/kp/301/timings")
printf '%s' "$timings_payload" |
  jq -e '.sourceMovieId == "301" and
    .sourceUrl == "https://timings.rte.net.ru/api/public/timings/301" and
    ([.entries[].kind] | index("interval") != null) and
    ([.entries[].kind] | index("clean-claim") != null)' >/dev/null

for _ in 1 2; do
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    --header 'X-Forwarded-For: 203.0.113.2' \
    "$api_url/v1/movies/search?q=negative-cache-probe")
  [[ "$status" == 502 ]]
done
[[ $(grep -Fc '/api/search/negative-cache-probe' "$upstream_log_path") == 1 ]]

for _ in 1 2; do
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    --header 'X-Forwarded-For: 203.0.113.13' \
    "$api_url/v1/movies/search?q=upstream-rate-limit-probe")
  [[ "$status" == 429 ]]
done
[[ $(grep -Fc '/api/search/upstream-rate-limit-probe' "$upstream_log_path") == 1 ]]
upstream_retry_headers=$(curl -sS -D - -o /dev/null \
  --header 'X-Forwarded-For: 203.0.113.13' \
  "$api_url/v1/movies/search?q=upstream-rate-limit-probe")
printf '%s' "$upstream_retry_headers" | tr -d '\r' | grep -Eq '^Retry-After: 60$'

for _ in 1 2; do
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    --header 'X-Forwarded-For: 203.0.113.3' \
    "$api_url/v1/movies/search?q=not-found-probe")
  [[ "$status" == 404 ]]
done
[[ $(grep -Fc '/api/search/not-found-probe' "$upstream_log_path") == 2 ]]

status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --header 'X-Forwarded-For: 203.0.113.7' \
  "$api_url/v1/movies/search?q=redirect-probe")
[[ "$status" == 502 ]]
[[ $(grep -Fc '/api/search/redirect-target' "$upstream_log_path") == 0 ]]

coalesce_statuses=$(seq 1 5 | xargs -P5 -I{} \
  curl -sS -o /dev/null -w '%{http_code}\n' \
  --header 'X-Forwarded-For: 203.0.113.4' \
  "$api_url/v1/movies/search?q=coalesce-probe")
[[ $(printf '%s\n' "$coalesce_statuses" | grep -c '^200$') == 5 ]]
[[ $(grep -Fc '/api/search/coalesce-probe' "$upstream_log_path") == 1 ]]

for query in 'cache normalization' $'cache\nnormalization'; do
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    --header 'X-Forwarded-For: 203.0.113.5' \
    --get --data-urlencode "q=$query" \
    "$api_url/v1/movies/search")
  [[ "$status" == 200 ]]
done
[[ $(grep -Fc '/api/search/cache%20normalization' "$upstream_log_path") == 1 ]]

seq 1 4 | xargs -P4 -I{} \
  curl -sS -o /dev/null -w '%{http_code}\n' \
  --header 'X-Forwarded-For: 203.0.113.6' \
  "$api_url/v1/movies/search?q=slow-probe-{}" >"$slow_status_path" &
slow_pid=$!
for _ in {1..120}; do
  if [[ $(grep -Fc '/api/search/slow-probe-' "$upstream_log_path") == 4 ]]; then
    break
  fi
  sleep 0.025
done
if [[ $(grep -Fc '/api/search/slow-probe-' "$upstream_log_path") != 4 ]]; then
  echo "timings api did not occupy all upstream slots" >&2
  exit 1
fi
status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --header 'X-Forwarded-For: 203.0.113.6' \
  "$api_url/v1/movies/search?q=busy-probe")
[[ "$status" == 503 ]]
wait "$slow_pid"
slow_pid=
[[ $(grep -c '^200$' "$slow_status_path") == 4 ]]

limited=false
for _ in {1..65}; do
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    --header 'X-Forwarded-For: 203.0.113.10' \
    "$api_url/v1/movies/search?q=")
  if [[ "$status" == 429 ]]; then
    limited=true
    break
  fi
  [[ "$status" == 400 ]]
done
[[ "$limited" == true ]]
status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --header 'X-Forwarded-For: 203.0.113.10' \
  "$api_url/v1/movies/search?q=private-query-limited")
[[ "$status" == 429 ]]
retry_headers=$(curl -sS -D - -o /dev/null \
  --header 'X-Forwarded-For: 203.0.113.10' \
  "$api_url/v1/movies/search?q=")
printf '%s' "$retry_headers" | tr -d '\r' | grep -Eq '^Retry-After: [1-9][0-9]*$'
status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --header 'X-Forwarded-For: 203.0.113.11' \
  "$api_url/v1/movies/search?q=")
[[ "$status" == 400 ]]

# Graceful shutdown flushes the console logger before the negative privacy check.
kill "$api_pid"
wait "$api_pid" || true
api_pid=
if grep -Fq 'private-query-' "$log_path"; then
  echo "timings api logged a private search query" >&2
  exit 1
fi

# Without an explicitly trusted proxy, forged X-Forwarded-For values must not
# create fresh limiter partitions for requests from the same socket address.
api_url=
CENSORPLAYER_RTE_BASE_URL="$upstream_url/api" \
ASPNETCORE_URLS=http://127.0.0.1:0 \
  dotnet run \
    --project "$repo_root/src/Censor.Timings.Api/Censor.Timings.Api.csproj" \
    --configuration Release \
    --no-restore >"$untrusted_log_path" 2>&1 &
api_pid=$!
for _ in {1..120}; do
  api_url=$(sed -n 's/^[[:space:]]*Now listening on: //p' "$untrusted_log_path" | tail -n 1)
  if [[ -n "$api_url" ]] && curl -fsS "$api_url/healthz" >/dev/null 2>&1; then
    break
  fi
  sleep 0.25
done
[[ -n "$api_url" ]]
limited=false
for index in {1..31}; do
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    --header "X-Forwarded-For: 203.0.113.$index" \
    "$api_url/v1/movies/search?q=")
  if [[ "$status" == 429 ]]; then
    limited=true
    break
  fi
  [[ "$status" == 400 ]]
done
[[ "$limited" == true ]]
kill "$api_pid"
wait "$api_pid" || true
api_pid=

echo "timings api smoke passed"
