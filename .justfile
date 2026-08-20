# Bitcoin related commands
mod bitcoin 'docker/bitcoin'

#####################
# Project variables #
#####################

# Version of this template
TEMPLATE_VERSION := "0.1.1"
# Project directory (relative to the justfile)
PROJECT_DIR := 'src'
# Docker directory
DOCKER_DIR := 'docker'
# Docker compose dev file name
DOCKER_COMPOSE_FILE := 'docker-compose.yml'

##################
# Just variables #
##################

# Fallback to a justfile in a parent directory
set fallback := true
# Load .env file in the current directory
set dotenv-load := true

###########
# Aliases #
###########

alias i := install
alias b := build
alias r := run
alias t := test
alias f := format
alias ddb := drop-db
alias am := add-migration
alias rm := remove-migration
alias du := docker-up
alias ddn := docker-down
alias drm := docker-rm
alias up := update-protos

#######
# All#
#######

# Everything necessary to install the project
[macos]
install:
    #!/usr/bin/env bash
    set -euxo pipefail
    # Add your installation steps here


##########
# Dotnet #
##########

build:
    cd {{PROJECT_DIR}} && dotnet build

run:
    ./docker/extract-macaroons.sh
    cd {{PROJECT_DIR}} && IS_DEV_ENVIRONMENT=true dotnet run

watch:
    ./docker/extract-macaroons.sh
    cd {{PROJECT_DIR}} && IS_DEV_ENVIRONMENT=true dotnet watch

stop:
    killall -9 NodeGuard

test:
    dotnet test

format:
    dotnet format

drop-db:
    cd {{PROJECT_DIR}} && dotnet ef database drop -f --context ApplicationDbContext
add-license-cs:
    go install github.com/fbiville/headache/cmd/headache@latest
    headache --configuration ./configuration-cs.json
add-migration name:
   cd {{PROJECT_DIR}} && dotnet ef migrations add --context ApplicationDbContext {{name}}
remove-migration:
    cd {{PROJECT_DIR}} && dotnet ef migrations remove --context ApplicationDbContext
# Mines a block a minute — set BITCOIND_CONTAINER to target an external regtest (see external-up)
mine:
    while true; do docker exec ${BITCOIND_CONTAINER:-polar-n1-backend} bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass -generate 1; sleep 60; done

# Update protobuf definitions from LND and Loop repositories
update-protos:
    ./src/Proto/update-protos.sh

##########
# Docker #
##########

# Builds and runs the development docker containers in the background, add DOCKER_COMPOSE_FILE to override the default file
docker-up *args:
    docker compose --profile polar --profile loop --profile 40swap -f {{DOCKER_COMPOSE_FILE}} up --build -d {{args}}

# Requires BITCOIND_CONTAINER (repo-root .env or inline), e.g.:
#   BITCOIND_CONTAINER=polar-n3-backend1 just external-up
# Brings up only NodeGuard's dependencies (postgres + nbxplorer) against an already-running regtest
external-up *args:
    #!/usr/bin/env bash
    set -euo pipefail
    if [ -z "${BITCOIND_CONTAINER:-}" ]; then
        echo "BITCOIND_CONTAINER is not set. Copy .env.example to .env and set it, or run:" >&2
        echo "  BITCOIND_CONTAINER=<bitcoind container> just external-up" >&2
        exit 1
    fi
    # nbxplorer runs in a container and NodeGuard on the host, so both reach the external
    # bitcoind through its published ports rather than through its compose network.
    BITCOIND_RPC_PORT=$(docker port "$BITCOIND_CONTAINER" 18443 | grep -m1 '^0\.0\.0\.0:' | cut -d: -f2)
    BITCOIND_P2P_PORT=$(docker port "$BITCOIND_CONTAINER" 18444 | grep -m1 '^0\.0\.0\.0:' | cut -d: -f2)
    if [ -z "$BITCOIND_RPC_PORT" ] || [ -z "$BITCOIND_P2P_PORT" ]; then
        echo "Could not resolve the published 18443/18444 ports of $BITCOIND_CONTAINER" >&2
        docker port "$BITCOIND_CONTAINER" >&2 || true
        exit 1
    fi
    echo "Using $BITCOIND_CONTAINER: RPC on host port $BITCOIND_RPC_PORT, P2P on $BITCOIND_P2P_PORT"
    export BITCOIND_CONTAINER BITCOIND_HOST=host.docker.internal BITCOIND_RPC_PORT BITCOIND_P2P_PORT
    docker compose --profile external -f {{DOCKER_COMPOSE_FILE}} up -d {{args}}

# Stops the containers started by external-up (leaves the external regtest network alone)
external-down:
    docker compose --profile external -f {{DOCKER_COMPOSE_FILE}} down

# Stops the development docker containers, add DOCKER_COMPOSE_FILE to override the default file
docker-down:
    docker compose --profile polar --profile loop --profile 40swap -f {{DOCKER_COMPOSE_FILE}} down

# Stops the development docker containers and removes the volumes, add DOCKER_COMPOSE_FILE to override the default file
docker-rm:
    docker compose --profile polar --profile loop --profile 40swap --profile e2e --profile mempool -f {{DOCKER_COMPOSE_FILE}} down -v

# Runs the option-B end-to-end rebalance test in containers: brings up the regtest stack + a live
# NodeGuard, then the runner opens a channel via gRPC and rebalances. Exit code = test result.
# Starts from a CLEAN slate (down -v first) — DbInitializer only funds the dev wallets when the DB
# has none, so a stale postgres volume would leave the wallet unfunded ("no UTXOs" on OpenChannel).
test-e2e:
    -docker compose --profile polar --profile e2e -f {{DOCKER_COMPOSE_FILE}} down -v --remove-orphans
    docker compose --profile polar --profile e2e -f {{DOCKER_COMPOSE_FILE}} run --rm --build e2e-runner
    docker compose --profile polar --profile e2e -f {{DOCKER_COMPOSE_FILE}} down -v --remove-orphans

##########
# Dapr #
##########

# Execute NodeGuard with a Dapr sidecar
dapr-run:
    dapr run --app-id nodeguard --app-port 50051 --app-protocol grpc --dapr-grpc-port 33601 -- dotnet run --project src/NodeGuard.csproj --launch-profile "NodeGuard local debug"
    
# Stop NodeGuard with Dapr sidecar and the server which stays running in the background
dapr-stop:
    ps -ef | grep '[d]apr.*--app-id nodeguard' | awk '{print $2}' | xargs -r kill -9 && killall -9 NodeGuard