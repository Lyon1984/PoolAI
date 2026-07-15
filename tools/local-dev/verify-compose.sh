#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repo_root/deploy/compose/compose.yaml"
toolchain="$repo_root/tools/local-dev/run-with-toolchain.sh"
api_port=${POOLAI_API_PORT:-8080}
mock_port=${POOLAI_MOCK_UPSTREAM_PORT:-4010}
mailpit_port=${POOLAI_MAILPIT_UI_PORT:-8025}

wait_for_http_status() {
    name="$1"
    url="$2"
    expected_status="$3"
    attempt=1

    while [ "$attempt" -le 60 ]; do
        actual_status=$(curl \
            --silent \
            --output /dev/null \
            --write-out '%{http_code}' \
            --connect-timeout 2 \
            --max-time 5 \
            "$url" 2>/dev/null || true)
        if [ "$actual_status" = "$expected_status" ]; then
            return 0
        fi
        attempt=$((attempt + 1))
        sleep 1
    done

    echo "verify-compose: $name did not return HTTP $expected_status within 60 attempts: $url" >&2
    return 1
}

verify_worker_stable() {
    container_id=$(
        "$toolchain" docker compose \
            --file "$compose_file" \
            ps --quiet worker
    )
    if [ -z "$container_id" ]; then
        echo "verify-compose: Worker container is missing." >&2
        return 1
    fi

    worker_state=$(
        "$toolchain" docker inspect \
            --format '{{.State.Status}}:{{.RestartCount}}' \
            "$container_id"
    )
    if [ "$worker_state" != "running:0" ]; then
        echo "verify-compose: Worker is not stably running without restarts: $worker_state" >&2
        return 1
    fi
}

set_ntp_control() {
    payload="$1"
    curl \
        --fail \
        --silent \
        --show-error \
        --request POST \
        --header 'content-type: application/json' \
        --data "$payload" \
        --connect-timeout 2 \
        --max-time 5 \
        "http://127.0.0.1:$mock_port/test-control/ntp" >/dev/null
}

verify_ntp_failure() {
    name="$1"
    payload="$2"
    set_ntp_control "$payload"
    wait_for_http_status "$name readiness failure" "http://127.0.0.1:$api_port/health/ready" 503
    wait_for_http_status "$name leaves liveness healthy" "http://127.0.0.1:$api_port/health/live" 200
}

wait_for_http_status "mock upstream" "http://127.0.0.1:$mock_port/healthz" 200
wait_for_http_status "Mailpit" "http://127.0.0.1:$mailpit_port/readyz" 200
wait_for_http_status "Api liveness" "http://127.0.0.1:$api_port/health/live" 200
verify_worker_stable

set_ntp_control '{"mode":"reset"}'
wait_for_http_status "Api readiness with synchronized SNTP" "http://127.0.0.1:$api_port/health/ready" 200

verify_ntp_failure "+6000ms SNTP offset" '{"mode":"offset","offsetMilliseconds":6000}'
verify_ntp_failure "-6000ms SNTP offset" '{"mode":"offset","offsetMilliseconds":-6000}'
verify_ntp_failure "dropped SNTP response" '{"mode":"drop"}'

set_ntp_control '{"mode":"reset"}'
wait_for_http_status "Api readiness after SNTP reset" "http://127.0.0.1:$api_port/health/ready" 200
verify_worker_stable

echo "Local Compose dependencies, stable Worker startup, live SNTP readiness failures, liveness isolation, and reset recovery are verified."
