#!/bin/bash

# Script to update protobuf definitions for NodeGuard
# Reads versions from Docker Compose files to ensure consistency

set -euo pipefail

# Get the project root directory (assuming this script is in src/Proto)
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

echo "Reading versions from Docker Compose files..."

# Extract LND version from docker/bitcoin/docker-compose.polar.yml
LND_IMAGE=$(go tool yq '.x-lnd-node.image' "$PROJECT_ROOT/docker/bitcoin/docker-compose.polar.yml")
LND_VERSION=$(echo "$LND_IMAGE" | grep -o 'lnd-v[0-9]\+\.[0-9]\+\.[0-9]\+-beta' | sed 's/lnd-//')

# Extract loopd version from docker/loop/docker-compose.yml
LOOPD_IMAGE=$(go tool yq '.x-loopd-service.image' "$PROJECT_ROOT/docker/loop/docker-compose.yml")
LOOPD_VERSION=$(echo "$LOOPD_IMAGE" | grep -o 'v[0-9]\+\.[0-9]\+\.[0-9]\+-beta')

echo "Found LND version: $LND_VERSION"
echo "Found loopd version: $LOOPD_VERSION"

# Define proto files to download
PROTO_DIR="$PROJECT_ROOT/src/Proto"

echo "Downloading proto files..."

# LND proto files (using raw.githubusercontent.com with refs/tags/)
echo "Downloading LND signer.proto..."
mkdir -p "$PROTO_DIR/signrpc"
curl -L "https://raw.githubusercontent.com/lightningnetwork/lnd/refs/tags/$LND_VERSION/lnrpc/signrpc/signer.proto" -o "$PROTO_DIR/signrpc/signer.proto"

echo "Downloading LND lightning.proto..."
curl -L "https://raw.githubusercontent.com/lightningnetwork/lnd/refs/tags/$LND_VERSION/lnrpc/lightning.proto" -o "$PROTO_DIR/lightning.proto"

echo "Downloading LND walletkit.proto..."
curl -L "https://raw.githubusercontent.com/lightningnetwork/lnd/refs/tags/$LND_VERSION/lnrpc/walletrpc/walletkit.proto" -o "$PROTO_DIR/walletkit.proto"

echo "Downloading LND common.proto..."
mkdir -p "$PROTO_DIR/swapserverrpc"
curl -L "https://raw.githubusercontent.com/lightninglabs/loop/refs/tags/$LOOPD_VERSION/swapserverrpc/common.proto" -o "$PROTO_DIR/swapserverrpc/common.proto"

# Loop proto file (rename client.proto to loop.proto)
echo "Downloading Loop client.proto (saving as loop.proto)..."
curl -L "https://raw.githubusercontent.com/lightninglabs/loop/refs/tags/$LOOPD_VERSION/looprpc/client.proto" -o "$PROTO_DIR/loop.proto"

echo "Proto files downloaded successfully!"