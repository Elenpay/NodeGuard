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
mine:
    while true; do docker exec polar-n1-backend1 bitcoin-cli -regtest -rpcuser=polaruser -rpcpassword=polarpass -generate 1; sleep 60; done

##########
# Docker #
##########

# Builds and runs the development docker containers in the background, add DOCKER_COMPOSE_FILE to override the default file
docker-up *args:
    docker compose --profile polar --profile loop -f {{DOCKER_COMPOSE_FILE}} up --build -d {{args}}

# Stops the development docker containers, add DOCKER_COMPOSE_FILE to override the default file
docker-down:
    docker compose --profile polar --profile loop -f {{DOCKER_COMPOSE_FILE}} down

# Stops the development docker containers and removes the volumes, add DOCKER_COMPOSE_FILE to override the default file
docker-rm:
    docker compose --profile polar --profile loop -f {{DOCKER_COMPOSE_FILE}} down -v

##########
# Dapr #
##########

# Execute NodeGuard with a Dapr sidecar
dapr-run:
    dapr run --app-id nodeguard --app-port 50051 --app-protocol grpc --dapr-grpc-port 33601 -- dotnet run --project src/NodeGuard.csproj --launch-profile "NodeGuard local debug"
    
# Stop NodeGuard with Dapr sidecar and the server which stays running in the background
dapr-stop:
    ps -ef | grep '[d]apr.*--app-id nodeguard' | awk '{print $2}' | xargs -r kill -9 && killall -9 NodeGuard