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

echo "Opening a channel from Alice to Bob"
lncli $ALICE openchannel --connect $BOB:9735 $BOB_PUBKEY --local_amt 16000000 --push_amt 8000000

echo "Opening a channel from Bob to Carol"
lncli $BOB openchannel --connect $CAROL:9735 $CAROL_PUBKEY --local_amt 16000000 --push_amt 8000000  

echo "Opening a channel from Carol to Alice"
lncli $CAROL openchannel --connect $ALICE:9735 $ALICE_PUBKEY --local_amt 16000000 --push_amt 8000000

echo "Confirming channels"
bitcoin_cli -generate 6 > /dev/null
