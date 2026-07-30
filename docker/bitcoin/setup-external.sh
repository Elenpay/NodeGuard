#!/bin/sh

# Prepares an externally-managed regtest bitcoind (e.g. one started by the Polar app) for
# NodeGuard. Unlike setup.sh this never funds nodes or opens channels — the network is assumed
# to be already set up. It only reconciles the miner wallet:
#
#   NodeGuard's DbInitializer does `unloadwallet ""` + `loadwallet "default"` on startup and then
#   issues wallet RPCs without -rpcwallet, so bitcoind must have exactly one loaded wallet and it
#   must be named `default`. Polar ships the unnamed ("") wallet, hence this reconciliation.
#
# Inputs (env): BITCOIND_CONTAINER, BITCOIND_RPCUSER, BITCOIND_RPCPASSWORD, EXTERNAL_MINE_BLOCKS,
#               EXTERNAL_MINE_TARGET_BTC, EXTERNAL_MINE_MAX_BLOCKS.

set -e

EXTERNAL_MINE_BLOCKS=${EXTERNAL_MINE_BLOCKS:-101}
EXTERNAL_MINE_TARGET_BTC=${EXTERNAL_MINE_TARGET_BTC:-100}
EXTERNAL_MINE_MAX_BLOCKS=${EXTERNAL_MINE_MAX_BLOCKS:-1000}

if [ -z "$BITCOIND_CONTAINER" ]; then
    echo "ERROR: BITCOIND_CONTAINER is not set."
    echo "       Set it to the name of your running bitcoind container, e.g.:"
    echo "         BITCOIND_CONTAINER=polar-n3-backend1 just external-up"
    echo "       or put it in the .env file at the repo root (see .env.example)."
    exit 1
fi

if ! docker ps --format '{{.Names}}' | grep -q "^${BITCOIND_CONTAINER}$"; then
    echo "ERROR: container '${BITCOIND_CONTAINER}' is not running. Running bitcoind containers:"
    docker ps --format '{{.Names}}\t{{.Image}}' | grep -i bitcoind || echo "  (none)"
    exit 1
fi

bitcoin_cli() {
    docker exec "$BITCOIND_CONTAINER" bitcoin-cli -regtest \
        -rpcuser="$BITCOIND_RPCUSER" -rpcpassword="$BITCOIND_RPCPASSWORD" "$@"
}

# Wallet-scoped calls, once `default` is the loaded wallet.
default_wallet_cli() {
    docker exec "$BITCOIND_CONTAINER" bitcoin-cli -regtest \
        -rpcuser="$BITCOIND_RPCUSER" -rpcpassword="$BITCOIND_RPCPASSWORD" -rpcwallet=default "$@"
}

echo "=== Checking ${BITCOIND_CONTAINER} ==="
CHAIN=$(bitcoin_cli getblockchaininfo | jq -r .chain)
if [ "$CHAIN" != "regtest" ]; then
    echo "ERROR: ${BITCOIND_CONTAINER} is on chain '${CHAIN}', NodeGuard's dev setup expects regtest."
    exit 1
fi
echo "chain=regtest height=$(bitcoin_cli getblockcount)"

echo "=== Reconciling the miner wallet ==="
# createwallet fails if it already exists on disk, loadwallet fails if already loaded: either way
# we end up with `default` loaded.
bitcoin_cli createwallet default >/dev/null 2>&1 || bitcoin_cli loadwallet default >/dev/null 2>&1 || true

if ! bitcoin_cli listwallets | jq -e 'index("default")' >/dev/null; then
    echo "ERROR: could not create or load a wallet named 'default'."
    bitcoin_cli listwallets
    exit 1
fi

BALANCE=$(default_wallet_cli getbalance)
echo "default wallet balance: ${BALANCE} BTC"

# NodeGuard's dev seeding sends 4 x 20 BTC out of the miner wallet, so make sure there is enough
# mature coin. Mining is additive: it never touches the balance of the other wallets.
if [ "$EXTERNAL_MINE_BLOCKS" -gt 0 ] 2>/dev/null; then
    mined=0
    # First batch establishes coinbase maturity (100 confirmations), then we top up in chunks —
    # on a chain that has already halved a few times one mature coinbase is not worth much.
    while awk -v b="$BALANCE" -v t="$EXTERNAL_MINE_TARGET_BTC" 'BEGIN { exit !(b < t) }'; do
        if [ "$mined" -ge "$EXTERNAL_MINE_MAX_BLOCKS" ]; then
            echo "WARNING: stopped after mining ${mined} blocks with ${BALANCE} BTC mature"
            echo "         (target ${EXTERNAL_MINE_TARGET_BTC} BTC). NodeGuard's dev wallet funding may fail;"
            echo "         raise EXTERNAL_MINE_MAX_BLOCKS or fund the 'default' wallet yourself."
            break
        fi

        batch=$([ "$mined" -eq 0 ] && echo "$EXTERNAL_MINE_BLOCKS" || echo 50)
        echo "Mining ${batch} blocks to the default wallet (mature balance ${BALANCE}/${EXTERNAL_MINE_TARGET_BTC} BTC)"
        default_wallet_cli -generate "$batch" >/dev/null
        mined=$((mined + batch))
        BALANCE=$(default_wallet_cli getbalance)
    done
    echo "default wallet balance: ${BALANCE} BTC, height $(bitcoin_cli getblockcount)"
fi

# Unload everything else last, so wallet RPCs without -rpcwallet are unambiguous: NodeGuard talks
# to bitcoind without -rpcwallet and bitcoind then requires exactly one loaded wallet.
# The wallet files stay on disk — reload one with `bitcoin-cli -regtest loadwallet <name>`.
# NOTE: `jq -r '.[]'` cannot emit Bitcoin Core's unnamed wallet (it is the empty string and word
# splitting drops it), so it is unloaded explicitly.
bitcoin_cli unloadwallet "" >/dev/null 2>&1 && echo "Unloaded the unnamed wallet" || true
for wallet in $(bitcoin_cli listwallets | jq -r '.[]'); do
    if [ "$wallet" != "default" ]; then
        echo "Unloading wallet '${wallet}'"
        bitcoin_cli unloadwallet "$wallet" || true
    fi
done
echo "Loaded wallets: $(bitcoin_cli listwallets | jq -c .)"

echo "=== Done, ${BITCOIND_CONTAINER} is ready for NodeGuard ==="
