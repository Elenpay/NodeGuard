#!/bin/bash

# Make sure that the script has been invoked with the node name as the first argument.
if [ "$#" -ne 1 ]; then
    echo "Usage: $0 <node-name>"
    exit 1
fi

echo "Initializing node $1"
NODE=$1
TARGET=/root/.lnd/data/chain/bitcoin/regtest

# Creates a wallet using the specified node name as seed.
if [ ! -d "$TARGET" ]; then
    echo "Initializing wallet for $NODE (TARGET directory does not exist)"
    lndinit -v init-wallet \
        --network regtest \
        --secret-source=file \
        --file.seed=/init/seed.$NODE \
        --file.wallet-password=/init/passwd \
        --init-file.output-wallet-dir=$TARGET
else
    echo "Skipping lndinit for $NODE (TARGET directory already exists: $TARGET)"
fi

echo "$NODE initialized"

# Copy files to shared directory for other containers to access (only for alice)
if [ "$NODE" = "alice" ]; then
    echo "Will copy files for $NODE after lnd starts"
    (
        # Wait for lnd to start and create fresh files
        while ! lncli -n regtest getinfo > /dev/null 2>&1; do
            sleep 1
        done
        echo "lnd is ready, copying fresh files for $NODE"
        mkdir -p /shared/lnd/$NODE
        cp $TARGET/admin.macaroon /shared/lnd/$NODE/
        cp /root/.lnd/tls.cert /shared/lnd/$NODE/
        chown -R 0:0 /shared
        chmod -R 755 /shared
        echo "Files copied for $NODE"
    ) &
fi

# Start lnd with the created wallet.
lnd --wallet-unlock-password-file=/init/passwd \
    --trickledelay=5000 \
    --alias=$NODE --externalip=$NODE --tlsextradomain=$NODE --tlsextradomain=polar-n1-$NODE \
    --listen=0.0.0.0:9735 --rpclisten=0.0.0.0:10009 --restlisten=0.0.0.0:8080 \
    --bitcoin.regtest --bitcoin.node=bitcoind \
    --bitcoind.rpchost=bitcoind --bitcoind.rpcuser=polaruser --bitcoind.rpcpass=polarpass \
    --bitcoind.zmqpubrawblock=tcp://bitcoind:28334 --bitcoind.zmqpubrawtx=tcp://bitcoind:28335 \
    --maxpendingchannels=10