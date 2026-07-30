#!/bin/sh
#
# E2E variant of docker/bitcoin/setup.sh for the OPTION-B rebalance e2e.
#
# Difference from setup.sh: it does NOT open the Alice->Bob channel — that channel is opened
# by NodeGuard itself via the gRPC OpenChannel API (so the e2e also covers channel opening).
# It still loads the bitcoind wallet, funds the LND nodes, opens Bob->Carol and Carol->Alice
# (the rest of the rebalance cycle), sets fee policies, and mines for confirmation + gossip.
#
# Runs in a container that has docker.sock + docker-cli (same pattern as the polar `setup`
# service), so it can `docker exec` into the polar containers.

ALICE=polar-n1-alice
BOB=polar-n1-bob
CAROL=polar-n1-carol
BACKEND=polar-n1-backend

lncli() {
    node=$1; shift; args=$@
    while true; do
        r=$(docker exec $node lncli -n regtest --tlscertpath /root/.lnd/tls.cert --macaroonpath /root/.lnd/data/chain/bitcoin/regtest/admin.macaroon $args)
        e=$?
        [ $e -eq 0 ] && echo $r && return 0
        >&2 echo "Command failed retrying..."
        sleep 1
    done
}
node_pubkey() { lncli $1 getinfo | jq -r .identity_pubkey; }
new_lnd_address() { lncli $1 newaddress p2wkh | jq -r .address; }
bitcoin_cli() { docker exec $BACKEND bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass -rpcwallet=default $@; }

echo "Ensuring a single loaded 'default' wallet on bitcoind"
docker exec $BACKEND bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass unloadwallet "" || true
WALLETS=$(bitcoin_cli listwallets | jq -r '.[] | select(. != "")')
for wallet in $WALLETS; do bitcoin_cli unloadwallet "$wallet"; done
bitcoin_cli createwallet default || true
bitcoin_cli loadwallet default || true

echo "Funding Alice, Bob and Carol"
bitcoin_cli generatetoaddress 5 $(new_lnd_address $ALICE)
bitcoin_cli generatetoaddress 5 $(new_lnd_address $BOB)
bitcoin_cli generatetoaddress 5 $(new_lnd_address $CAROL)
echo "Maturing blocks"
bitcoin_cli -generate 100 > /dev/null

ALICE_PUBKEY=$(node_pubkey $ALICE)
BOB_PUBKEY=$(node_pubkey $BOB)
CAROL_PUBKEY=$(node_pubkey $CAROL)

# Same fee-policy topology as setup.sh; only the Alice->Bob open is omitted (NodeGuard opens it).
BOB_TO_CAROL_LOCAL=16000000
BOB_TO_CAROL_PUSH=6000000
CAROL_TO_ALICE_LOCAL=16000000
CAROL_TO_ALICE_PUSH=10000000

echo "Opening Bob -> Carol (middle hop)"
lncli $BOB openchannel --connect $CAROL:9735 $CAROL_PUBKEY --local_amt $BOB_TO_CAROL_LOCAL --push_amt $BOB_TO_CAROL_PUSH

echo "Opening Carol -> Alice (return path)"
lncli $CAROL openchannel --connect $ALICE:9735 $ALICE_PUBKEY --local_amt $CAROL_TO_ALICE_LOCAL --push_amt $CAROL_TO_ALICE_PUSH

# NodeGuard opens the Alice->Bob channel itself (option B), so it never ran an `openchannel --connect`
# to learn Bob's address. Bob's node_announcement (with address) doesn't reach Alice over gossip when
# they share no channel, so pre-connect them here — NodeGuard's ConnectToPeer then sees them connected.
echo "Connecting Alice -> Bob as peers"
lncli $ALICE connect $BOB_PUBKEY@bob:9735 || true

echo "Confirming channels"
bitcoin_cli -generate 6 > /dev/null

# Distinct outbound fees so routing cost is visible: Bob 400 ppm + Carol 600 ppm = ~1000 ppm.
echo "Setting outbound fee policies"
lncli $ALICE updatechanpolicy --base_fee_msat 0 --fee_rate_ppm 1000 --time_lock_delta 40
lncli $BOB updatechanpolicy --base_fee_msat 0 --fee_rate_ppm 400 --time_lock_delta 40
lncli $CAROL updatechanpolicy --base_fee_msat 0 --fee_rate_ppm 600 --time_lock_delta 40

echo "Mining gossip blocks"
bitcoin_cli -generate 3 > /dev/null
echo "setup-e2e done (Alice->Bob is opened later by NodeGuard via gRPC)"
