#!/bin/sh
#
# Entrypoint wrapper for the e2e NodeGuard container. Sources the env file produced by the
# extract-env service (LND hosts/macaroons/pubkeys from the mounted data volumes) so NodeGuard's
# Constants pick them up as real environment variables, then launches the app.
set -e

ENV_FILE=/shared/nodeguard-macaroons.env
echo "Waiting for $ENV_FILE from extract-env..."
i=0
until [ -s "$ENV_FILE" ] || [ $i -ge 60 ]; do i=$((i+1)); sleep 2; done
if [ -s "$ENV_FILE" ]; then
    set -a; . "$ENV_FILE"; set +a
    echo "Loaded LND connection env for ALICE/BOB/CAROL."
else
    echo "WARNING: $ENV_FILE missing/empty; NodeGuard may fail to reach LND." >&2
fi

exec dotnet NodeGuard.dll
