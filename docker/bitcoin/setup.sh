#!/bin/sh

# LND nodes.
ALICE=polar-n1-alice
BOB=polar-n1-bob
CAROL=polar-n1-carol

# Bitcoind.
BACKEND=polar-n1-backend

lncli() {
    node=$1
    shift
    args=$@

    # LND might not be ready to accept calls so we'll rerun until it is.
    while true; do
        r=$(docker exec $node lncli -n regtest --tlscertpath /root/.lnd/tls.cert --macaroonpath /root/.lnd/data/chain/bitcoin/regtest/admin.macaroon $args)
        e=$?

        [[ $e -eq 0 ]] && echo $r && return 0

        >&2 echo "Command failed retrying..."
        sleep 1
    done
}

node_pubkey() {
    lncli $1 getinfo | jq -r .identity_pubkey
}

new_lnd_address() {
    lncli $1 newaddress p2wkh | jq -r .address
}

bitcoin_cli() {
    docker exec $BACKEND bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass -rpcwallet=default $@ 
}

# list and unload all wallets
echo "Unloading wallets"

echo $(bitcoin_cli listwallets)
# Handle empty wallet specially since bitcoin_cli function uses -rpcwallet=default
docker exec $BACKEND bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass unloadwallet "" || true
echo $(bitcoin_cli listwallets)
WALLETS=$(bitcoin_cli listwallets | jq -r '.[] | select(. != "")')
for wallet in $WALLETS; do
    echo "Unloading wallet $wallet"
    bitcoin_cli unloadwallet "$wallet"
done
echo $(bitcoin_cli listwallets)

# Make sure we always have a single wallet named 'default'
echo "Creating and loading default wallet"
bitcoin_cli createwallet default || true
bitcoin_cli loadwallet default || true
echo $(bitcoin_cli listwallets)

echo "Sending coinbase to Alice, Bob and Carol"
bitcoin_cli generatetoaddress 5 $(new_lnd_address $ALICE)
bitcoin_cli generatetoaddress 5 $(new_lnd_address $BOB)
bitcoin_cli generatetoaddress 5 $(new_lnd_address $CAROL)

echo "Maturing blocks"
bitcoin_cli -generate 100 > /dev/null


ALICE_PUBKEY=$(node_pubkey $ALICE)
BOB_PUBKEY=$(node_pubkey $BOB)
CAROL_PUBKEY=$(node_pubkey $CAROL)

# Topology for rebalance-friendly regtest:
#
#                  (~13M Alice / ~3M Bob)            (~10M Bob / ~6M Carol)
#   Alice ---------------------> Bob ---------------------> Carol
#     ^                                                       |
#     |  (~6M Alice / ~10M Carol — Alice has real inbound)    |
#     +-----------------------------------------------------
ALICE_TO_BOB_LOCAL=16000000
ALICE_TO_BOB_PUSH=3000000
BOB_TO_CAROL_LOCAL=16000000
BOB_TO_CAROL_PUSH=6000000
CAROL_TO_ALICE_LOCAL=16000000
CAROL_TO_ALICE_PUSH=10000000

echo "Opening a channel from Alice to Bob (heavy on Alice — drain source for rebalance tests)"
lncli $ALICE openchannel --connect $BOB:9735 $BOB_PUBKEY --local_amt $ALICE_TO_BOB_LOCAL --push_amt $ALICE_TO_BOB_PUSH

echo "Opening a channel from Bob to Carol (middle hop, balanced)"
lncli $BOB openchannel --connect $CAROL:9735 $CAROL_PUBKEY --local_amt $BOB_TO_CAROL_LOCAL --push_amt $BOB_TO_CAROL_PUSH

echo "Opening a channel from Carol to Alice (return path — Alice gets ~10M inbound from Carol)"
lncli $CAROL openchannel --connect $ALICE:9735 $ALICE_PUBKEY --local_amt $CAROL_TO_ALICE_LOCAL --push_amt $CAROL_TO_ALICE_PUSH

echo "Confirming channels"
bitcoin_cli -generate 6 > /dev/null

# Outbound fee rates are distinct per node so routing costs are non-trivial and
# visible in test assertions:
#   Alice:  1000 ppm (outbound, not paid by Alice on her own circular payment)
#   Bob:     400 ppm (paid when forwarding Alice→Carol leg)
#   Carol:   600 ppm (paid when forwarding Carol→Alice leg)
# A circular rebalance Alice→Bob→Carol→Alice costs Bob(400) + Carol(600) = ~1000 ppm
# of the forwarded amount. On 1 000 000 sats that is 1 000 sats in fees — clearly
# non-zero and easy to assert against. Base fees are 0 on all nodes.
echo "Setting outbound fee policies on all nodes"
# Fees are intentionally distinct so routing costs are visible in test assertions:
#   Alice -> Bob -> Carol -> Alice costs Bob's fee (400 ppm) + Carol's fee (600 ppm) = ~1000 ppm total.
#   All base fees are 0 so the only cost is proportional to the forwarded amount.
lncli $ALICE updatechanpolicy --base_fee_msat 0 --fee_rate_ppm 1000 --time_lock_delta 40
lncli $BOB updatechanpolicy --base_fee_msat 0 --fee_rate_ppm 400 --time_lock_delta 40
lncli $CAROL updatechanpolicy --base_fee_msat 0 --fee_rate_ppm 600 --time_lock_delta 40

echo "Mining a few extra blocks so gossip can propagate"
bitcoin_cli -generate 3 > /dev/null