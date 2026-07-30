#!/bin/sh
#
# Writes nodeguard-macaroons.env for the e2e NodeGuard container, using the LND data volumes
# mounted read-only (creds) + the LND gRPC over the compose network (for the node pubkey).
# No docker.sock. Runs in the LND image (has lncli + busybox od).
#
# Output (consumed by the nodeguard entrypoint via `set -a; . file`):
#   <NODE>_HOST     internal compose service name :10009  (e.g. alice:10009)
#   <NODE>_MACAROON admin.macaroon hex
#   <NODE>_PUBKEY   identity pubkey (from `lncli getinfo` over the network)
set -e

OUT=/shared/nodeguard-macaroons.env
: > "$OUT"

emit() {                 # emit <ENVNAME> <service> <mountdir>
    name=$1; svc=$2; dir=$3
    mac="$dir/data/chain/bitcoin/regtest/admin.macaroon"
    tls="$dir/tls.cert"
    # Wait for LND to answer (it may still be starting).
    until lncli -n regtest --rpcserver="$svc:10009" --macaroonpath "$mac" --tlscertpath "$tls" getinfo >/tmp/info 2>/dev/null; do
        echo "waiting for $svc getinfo..."; sleep 2
    done
    pubkey=$(grep identity_pubkey /tmp/info | head -1 | cut -d'"' -f4)
    hex=$(od -A n -v -t x1 "$mac" | tr -d ' \n')
    {
        echo "${name}_HOST=\"$svc:10009\""
        echo "${name}_MACAROON=\"$hex\""
        echo "${name}_PUBKEY=\"$pubkey\""
    } >> "$OUT"
    echo "wrote $name (pubkey=$pubkey)"
}

emit ALICE alice /lnd/alice
emit BOB   bob   /lnd/bob
emit CAROL carol /lnd/carol

echo "=== $OUT ==="
cat "$OUT"
