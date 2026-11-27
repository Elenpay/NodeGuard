#!/bin/sh
# One-shot setup for the local Spark network on NodeGuard's Polar chain. Idempotent.
#  1. Spark liquidity for the SSP (exact leaves, via its admin funding API)
#  2. a channel alice -> SSP Lightning node, so NodeGuard's LND can pay Spark invoices
# The SSP pays cooperative exits from bitcoind's default wallet: a second loaded wallet would break every
# bitcoind client that does not name its wallet (NodeGuard's dev funding at startup among them).
# Funding logic ported from benthecarman/open-ssp e2e/fund-ssp.mjs (MIT).
set -eu

SSP=http://spark-ssp:5000
TOKEN="${SPARK_ADMIN_TOKEN:?}"
BACKEND=polar-n1-backend
ALICE=polar-n1-alice
LDK=$(docker ps -q --filter label=com.docker.compose.service=spark-ldk-server | head -1)

btc() { docker exec "$BACKEND" bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass "$@"; }
lncli() { docker exec "$ALICE" lncli -n regtest --tlscertpath /root/.lnd/tls.cert --macaroonpath /root/.lnd/data/chain/bitcoin/regtest/admin.macaroon "$@"; }
ldk() { docker exec "$LDK" sh -c 'ldk-server-cli --base-url localhost:3536 --api-key "$(od -A n -t x1 /data/regtest/api_key | tr -d " \n")" --tls-cert /data/tls.crt "$@"' ldk "$@"; }
mine() { btc -rpcwallet=default -generate "$1" > /dev/null; }
admin() { curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" ${2:+-d "$2"} "$SSP$1"; }

# The SSP serves deposit addresses only once the operators have generated their signing keys, a while after
# it starts answering /identity
deposit_address() {
  for _ in $(seq 1 100); do
    address=$(admin /admin/spark/deposit-address 2>/dev/null | jq -r '.address // empty' || true)
    if [ -n "$address" ]; then echo "$address"; return 0; fi
    sleep 3
  done
  echo "the SSP served no deposit address" >&2
  return 1
}

btc loadwallet default > /dev/null 2>&1 || true

# The SSP refuses to start until bitcoind's default wallet exists, so wait for it only now.
echo "waiting for the SSP..."
until curl -sf "$SSP/identity" > /dev/null; do sleep 3; done

echo "1. SSP Spark liquidity"
AVAILABLE=$(curl -sf -H "Authorization: Bearer $TOKEN" "$SSP/status" | jq -r '.spark.available_sats // 0')
if [ "$AVAILABLE" -lt 1000000 ]; then
  : > /tmp/deposits
  for amount in 1000 2000 4000 8000 16000 32000 64000 128000 256000 512000 1024000; do
    for _ in 1 2; do
      address=$(deposit_address)
      txid=$(btc -rpcwallet=default sendtoaddress "$address" "$(echo "$amount" | awk '{printf "%.8f", $1/100000000}')")
      echo "$txid $address" >> /tmp/deposits
    done
  done
  mine 3
  while read -r txid address; do
    tx=$(btc getrawtransaction "$txid" true)
    vout=$(echo "$tx" | jq --arg a "$address" '.vout[] | select(.scriptPubKey.address == $a) | .n')
    admin /admin/spark/claim-deposit "$(jq -cn --arg h "$(echo "$tx" | jq -r .hex)" --argjson v "$vout" '{transaction_hex: $h, vout: $v}')" > /dev/null
  done < /tmp/deposits
fi
curl -sf -H "Authorization: Bearer $TOKEN" "$SSP/status" | jq -c '{spark_available_sats: .spark.available_sats}'

echo "2. channel alice -> SSP node"
LDK_PUBKEY=$(ldk get-node-info | jq -r .node_id)
# LDK rejects inbound anchor channels unless it holds >= 25k sats on-chain for fee bumping.
if [ "$(ldk get-balances | jq -r '.spendable_onchain_balance_sats // 0')" -lt 50000 ]; then
  btc -rpcwallet=default sendtoaddress "$(ldk onchain-receive | jq -r .address)" 0.01 > /dev/null
  mine 1
  until [ "$(ldk get-balances | jq -r '.spendable_onchain_balance_sats // 0')" -ge 50000 ]; do sleep 3; done
fi
if ! lncli listchannels | jq -e --arg p "$LDK_PUBKEY" '.channels[] | select(.remote_pubkey == $p)' > /dev/null; then
  lncli connect "$LDK_PUBKEY@spark-ldk-server:9735" > /dev/null 2>&1 || true
  lncli openchannel --node_key "$LDK_PUBKEY" --local_amt 5000000 > /dev/null
  mine 6
fi
until lncli listchannels | jq -e --arg p "$LDK_PUBKEY" '.channels[] | select(.remote_pubkey == $p and .active)' > /dev/null; do
  sleep 3
done
echo "spark setup done: alice has an active channel to $LDK_PUBKEY"
