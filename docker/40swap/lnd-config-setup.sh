#!/bin/sh
set -e

echo "Waiting for alice LND to be ready..."
sleep 5

# Extract LND connection info from alice node (adjust socket/port as needed)
lnd_socket="alice:10009"
lnd_cert=$(cat /alice_lnd_data/tls.cert | base64 -w0 2>/dev/null || cat /alice_lnd_data/tls.cert | base64)
lnd_macaroon=$(cat /alice_lnd_data/data/chain/bitcoin/regtest/admin.macaroon | base64 -w0 2>/dev/null || cat /alice_lnd_data/data/chain/bitcoin/regtest/admin.macaroon | base64)

# Create directory if it doesn't exist
mkdir -p /etc/40swap

# Copy the template config
cp /config-template/40swap.conf.yaml /etc/40swap/40swap.conf.yaml

# Append LND configuration to the config file
cat >> /etc/40swap/40swap.conf.yaml <<EOF
lnd:
  socket: $lnd_socket
  cert: $lnd_cert
  macaroon: $lnd_macaroon
EOF

echo "Config generated at /etc/40swap/40swap.conf.yaml"
cat /etc/40swap/40swap.conf.yaml

# Keep container running to show completion
echo "Setup complete. Container will exit."
