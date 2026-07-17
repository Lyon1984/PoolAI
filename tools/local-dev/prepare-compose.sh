#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
tools_dir="$repo_root/.tools"
compose_state_dir="$tools_dir/compose"
secret_dir="$compose_state_dir/secrets"

fail() {
    echo "prepare-compose: $*" >&2
    exit 1
}

for private_directory in "$tools_dir" "$compose_state_dir" "$secret_dir"; do
    if [ -L "$private_directory" ]; then
        fail "refusing symbolic-link private directory: $private_directory"
    fi
done

umask 077
mkdir -p "$secret_dir"
chmod 700 "$tools_dir" "$compose_state_dir" "$secret_dir"

ensure_regular_secret() {
    path="$1"
    if [ -L "$path" ]; then
        fail "refusing symbolic-link secret path: $path"
    fi
    if [ -e "$path" ] && { [ ! -f "$path" ] || [ ! -s "$path" ]; }; then
        fail "existing secret is not a non-empty regular file: $path"
    fi
}

ensure_random_hex() {
    name="$1"
    path="$secret_dir/$name"
    ensure_regular_secret "$path"
    if [ ! -e "$path" ]; then
        temporary=$(mktemp "$secret_dir/.${name}.XXXXXX")
        openssl rand -hex 32 > "$temporary"
        chmod 600 "$temporary"
        mv "$temporary" "$path"
    fi
    chmod 600 "$path"
}

ensure_random_base64() {
    name="$1"
    path="$secret_dir/$name"
    ensure_regular_secret "$path"
    if [ ! -e "$path" ]; then
        temporary=$(mktemp "$secret_dir/.${name}.XXXXXX")
        openssl rand -base64 32 > "$temporary"
        chmod 600 "$temporary"
        mv "$temporary" "$path"
    fi
    chmod 600 "$path"
}

ensure_exact_secret() {
    name="$1"
    expected_value="$2"
    path="$secret_dir/$name"
    ensure_regular_secret "$path"

    temporary=$(mktemp "$secret_dir/.${name}.XXXXXX")
    printf '%s\n' "$expected_value" > "$temporary"
    chmod 600 "$temporary"

    if [ -e "$path" ]; then
        if ! cmp -s "$temporary" "$path"; then
            # Connection/config files are deterministic derivatives of the
            # preserved random secrets. Replace only the derivative when the
            # reviewed local format evolves; never rotate the source secret.
            mv "$temporary" "$path"
        else
            rm -f "$temporary"
        fi
    else
        mv "$temporary" "$path"
    fi
    chmod 600 "$path"
}

ensure_random_hex postgres-superuser-password
ensure_random_hex postgres-api-password
ensure_random_hex postgres-worker-password
ensure_random_hex postgres-migrator-password
ensure_random_hex redis-password
ensure_random_base64 auth-jwt-signing-key
ensure_random_base64 auth-refresh-token-current-pepper
ensure_random_base64 auth-password-reset-rate-scope-pepper
ensure_random_base64 auth-token-hash-current-pepper
ensure_random_base64 auth-totp-recovery-code-pepper
ensure_random_base64 auth-login-rate-scope-pepper
ensure_random_base64 api-keys-current-pepper
ensure_random_base64 idempotency-request-hash-pepper
ensure_random_base64 envelope-current-key

ensure_distinct_secrets() {
    while [ "$#" -gt 1 ]; do
        current="$1"
        current_value=$(tr -d '\r\n' < "$current")
        shift
        for candidate in "$@"; do
            candidate_value=$(tr -d '\r\n' < "$candidate")
            if [ "$current_value" = "$candidate_value" ]; then
                fail "purpose-specific secrets must be distinct: $(basename "$current") and $(basename "$candidate")"
            fi
        done
    done
}

ensure_distinct_secrets \
    "$secret_dir/auth-jwt-signing-key" \
    "$secret_dir/auth-refresh-token-current-pepper" \
    "$secret_dir/auth-password-reset-rate-scope-pepper" \
    "$secret_dir/auth-token-hash-current-pepper" \
    "$secret_dir/auth-totp-recovery-code-pepper" \
    "$secret_dir/auth-login-rate-scope-pepper" \
    "$secret_dir/api-keys-current-pepper" \
    "$secret_dir/idempotency-request-hash-pepper" \
    "$secret_dir/envelope-current-key"

postgres_api_password=$(tr -d '\r\n' < "$secret_dir/postgres-api-password")
postgres_worker_password=$(tr -d '\r\n' < "$secret_dir/postgres-worker-password")
postgres_migrator_password=$(tr -d '\r\n' < "$secret_dir/postgres-migrator-password")
redis_password=$(tr -d '\r\n' < "$secret_dir/redis-password")

ensure_exact_secret api-postgres-connection-string \
    "Host=postgres;Port=5432;Database=poolai;Username=poolai_api;Password=$postgres_api_password;SSL Mode=Disable;Include Error Detail=false"
ensure_exact_secret worker-postgres-connection-string \
    "Host=postgres;Port=5432;Database=poolai;Username=poolai_worker;Password=$postgres_worker_password;SSL Mode=Disable;Include Error Detail=false"
ensure_exact_secret migrator-postgres-connection-string \
    "Host=postgres;Port=5432;Database=poolai;Username=poolai_migrator;Password=$postgres_migrator_password;SSL Mode=Disable;Include Error Detail=false"
ensure_exact_secret redis-connection-string \
    "redis:6379,user=poolai,password=$redis_password,ssl=false,abortConnect=false"
ensure_exact_secret redis.conf \
    "bind 0.0.0.0
protected-mode yes
port 6379
appendonly yes
appendfsync everysec
save 60 1
maxmemory-policy noeviction
user default off
user poolai on >$redis_password ~poolai:r1:local-compose:* &poolai:r1:local-compose:* +@all -@admin -config -module -flushall -flushdb -keys -script|flush"

ca_key="$secret_dir/mock-smtp-ca-key.pem"
ca_cert="$secret_dir/mock-smtp-ca.pem"
smtp_key="$secret_dir/mock-smtp-key.pem"
smtp_cert="$secret_dir/mock-smtp-cert.pem"

tls_present=0
for tls_file in "$ca_key" "$ca_cert" "$smtp_key" "$smtp_cert"; do
    ensure_regular_secret "$tls_file"
    if [ -e "$tls_file" ]; then
        tls_present=$((tls_present + 1))
    fi
done

if [ "$tls_present" -ne 0 ] && [ "$tls_present" -ne 4 ]; then
    fail "local SMTP TLS material is incomplete; refusing to overwrite a partial trust set"
fi

if [ "$tls_present" -eq 0 ]; then
    tls_work=$(mktemp -d "$secret_dir/.smtp-tls.XXXXXX")
    cleanup_tls_work() {
        rm -f \
            "$tls_work/ca-key.pem" \
            "$tls_work/ca.pem" \
            "$tls_work/ca.srl" \
            "$tls_work/smtp-key.pem" \
            "$tls_work/smtp.csr" \
            "$tls_work/smtp-extensions.cnf" \
            "$tls_work/smtp-cert.pem"
        rmdir "$tls_work" 2>/dev/null || true
    }
    trap cleanup_tls_work EXIT
    trap 'cleanup_tls_work; exit 1' HUP INT TERM

    openssl genpkey \
        -algorithm RSA \
        -pkeyopt rsa_keygen_bits:3072 \
        -out "$tls_work/ca-key.pem" >/dev/null 2>&1
    openssl req \
        -x509 \
        -new \
        -key "$tls_work/ca-key.pem" \
        -sha256 \
        -days 825 \
        -subj "/CN=PoolAI Local Compose CA" \
        -addext "basicConstraints=critical,CA:TRUE" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" \
        -out "$tls_work/ca.pem"

    openssl genpkey \
        -algorithm RSA \
        -pkeyopt rsa_keygen_bits:3072 \
        -out "$tls_work/smtp-key.pem" >/dev/null 2>&1
    openssl req \
        -new \
        -key "$tls_work/smtp-key.pem" \
        -subj "/CN=mock-smtp" \
        -addext "subjectAltName=DNS:mock-smtp,DNS:localhost" \
        -out "$tls_work/smtp.csr"
    printf '%s\n' \
        "basicConstraints=critical,CA:FALSE" \
        "keyUsage=critical,digitalSignature,keyEncipherment" \
        "extendedKeyUsage=serverAuth" \
        "subjectAltName=DNS:mock-smtp,DNS:localhost" \
        > "$tls_work/smtp-extensions.cnf"
    openssl x509 \
        -req \
        -in "$tls_work/smtp.csr" \
        -CA "$tls_work/ca.pem" \
        -CAkey "$tls_work/ca-key.pem" \
        -CAcreateserial \
        -sha256 \
        -days 825 \
        -extfile "$tls_work/smtp-extensions.cnf" \
        -out "$tls_work/smtp-cert.pem" >/dev/null 2>&1

    openssl verify -CAfile "$tls_work/ca.pem" "$tls_work/smtp-cert.pem" >/dev/null
    openssl x509 -in "$tls_work/smtp-cert.pem" -noout -text \
        | grep -q 'DNS:mock-smtp' \
        || fail "generated SMTP certificate is missing the mock-smtp SAN"

    mv "$tls_work/ca-key.pem" "$ca_key"
    mv "$tls_work/ca.pem" "$ca_cert"
    mv "$tls_work/smtp-key.pem" "$smtp_key"
    mv "$tls_work/smtp-cert.pem" "$smtp_cert"
    cleanup_tls_work
    trap - EXIT HUP INT TERM
fi

chmod 600 "$ca_key" "$smtp_key"
chmod 644 "$ca_cert" "$smtp_cert"
openssl x509 -checkend 86400 -noout -in "$ca_cert" \
    || fail "local SMTP CA expires in less than 24 hours"
openssl x509 -checkend 86400 -noout -in "$smtp_cert" \
    || fail "local SMTP certificate expires in less than 24 hours"
openssl verify -CAfile "$ca_cert" "$smtp_cert" >/dev/null
openssl x509 -in "$smtp_cert" -noout -text \
    | grep -q 'DNS:mock-smtp' \
    || fail "SMTP certificate is missing the mock-smtp SAN"

system_ca_bundle=${POOLAI_SYSTEM_CA_BUNDLE:-}
if [ -z "$system_ca_bundle" ]; then
    for candidate in /etc/ssl/certs/ca-certificates.crt /etc/ssl/cert.pem; do
        if [ -s "$candidate" ]; then
            system_ca_bundle="$candidate"
            break
        fi
    done
fi

if [ -z "$system_ca_bundle" ] || [ ! -r "$system_ca_bundle" ] || [ ! -s "$system_ca_bundle" ]; then
    fail "no system CA bundle found; set POOLAI_SYSTEM_CA_BUNDLE to a readable PEM bundle"
fi
if ! grep -q -- '-----BEGIN CERTIFICATE-----' "$system_ca_bundle"; then
    fail "system CA bundle is not PEM encoded: $system_ca_bundle"
fi

combined_bundle="$secret_dir/local-compose-ca-bundle.pem"
ensure_regular_secret "$combined_bundle"
bundle_temporary=$(mktemp "$secret_dir/.local-compose-ca-bundle.XXXXXX")
{
    cat "$system_ca_bundle"
    printf '\n'
    cat "$ca_cert"
} > "$bundle_temporary"
chmod 644 "$bundle_temporary"

if [ -e "$combined_bundle" ]; then
    if ! cmp -s "$bundle_temporary" "$combined_bundle"; then
        mv "$bundle_temporary" "$combined_bundle"
    else
        rm -f "$bundle_temporary"
    fi
else
    mv "$bundle_temporary" "$combined_bundle"
fi
chmod 644 "$combined_bundle"

# Compose file-backed secrets are bind mounts: uid/gid/mode overrides are not
# applied by Compose. These exact source files therefore need a read bit for
# non-root container users. Their complete host path remains protected by the
# 0700 .tools/compose/secrets directory chain. Files not mounted into a
# container remain 0600.
chmod 644 \
    "$secret_dir/postgres-superuser-password" \
    "$secret_dir/postgres-api-password" \
    "$secret_dir/postgres-worker-password" \
    "$secret_dir/postgres-migrator-password" \
    "$secret_dir/api-postgres-connection-string" \
    "$secret_dir/worker-postgres-connection-string" \
    "$secret_dir/migrator-postgres-connection-string" \
    "$secret_dir/redis-password" \
    "$secret_dir/redis.conf" \
    "$secret_dir/redis-connection-string" \
    "$secret_dir/auth-jwt-signing-key" \
    "$secret_dir/auth-refresh-token-current-pepper" \
    "$secret_dir/auth-password-reset-rate-scope-pepper" \
    "$secret_dir/auth-token-hash-current-pepper" \
    "$secret_dir/auth-totp-recovery-code-pepper" \
    "$secret_dir/auth-login-rate-scope-pepper" \
    "$secret_dir/api-keys-current-pepper" \
    "$secret_dir/idempotency-request-hash-pepper" \
    "$secret_dir/envelope-current-key" \
    "$secret_dir/local-compose-ca-bundle.pem" \
    "$secret_dir/mock-smtp-cert.pem" \
    "$secret_dir/mock-smtp-key.pem"
chmod 600 "$ca_key" "$ca_cert"

echo "Prepared local Compose secrets and trust bundle in $secret_dir"
echo "Secret values were not printed. Normal Compose down/up preserves this directory and named volumes."
