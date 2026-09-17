#!/bin/bash

# Script to extract admin macaroons from LND and Loopd containers
# This script generates environment variables for NodeGuard C# application
#
# With no configuration it targets the in-repo polar stack (`docker compose --profile polar`).
# To target an already-running regtest network instead (e.g. one started from the Polar app),
# set these in the .env file at the repo root or in the environment:
#
#   MANAGED_NODES       comma/space separated LND container names, in the order they should be
#                       mapped onto NodeGuard's dev node slots, e.g.
#                       MANAGED_NODES=polar-n3-alice,polar-n3-bob,polar-n3-carol
#                       Endpoints are resolved from each container's published gRPC port.
#   BITCOIND_CONTAINER  name of the running bitcoind container, e.g. polar-n3-backend1. Its
#                       published RPC/P2P ports are written out so NodeGuard talks to that node
#                       instead of the in-repo one.

# LND's directory inside the container: the in-repo lndinit image runs as root out of /root/.lnd,
# the Polar app's image runs as the `lnd` user out of /home/lnd/.lnd.
LND_ROOT_CANDIDATES="/root/.lnd /home/lnd/.lnd /lnd"
LOOP_ROOT="/root/.loop"
LND_GRPC_PORT=10009
BITCOIND_RPC_PORT_INTERNAL=18443
BITCOIND_P2P_PORT_INTERNAL=18444

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}=== NodeGuard Macaroon Extractor ===${NC}"

# Pick up MANAGED_NODES / BITCOIND_CONTAINER from the repo-root .env when the script is invoked
# directly (just already loads it, VS Code tasks don't). Values already in the environment win.
load_dotenv() {
    local dotenv="$1"
    [ -f "$dotenv" ] || return 0
    while IFS= read -r line; do
        case "$line" in
            ''|'#'*) continue ;;
        esac
        local key="${line%%=*}"
        local value="${line#*=}"
        # Strip surrounding quotes and any trailing whitespace/comment-free remainder.
        value="${value%\"}"; value="${value#\"}"
        value="${value%\'}"; value="${value#\'}"
        if [ -n "${!key:-}" ]; then
            continue
        fi
        export "${key}=${value}"
    done < "$dotenv"
}

# Published host port for a container port, e.g. published_port polar-n3-alice 10009 -> 10004
published_port() {
    local container_name=$1
    local internal_port=$2

    docker port "${container_name}" "${internal_port}" 2>/dev/null \
        | grep -m1 '^0\.0\.0\.0:' \
        | cut -d: -f2
}

# polar-n3-alice -> ALICE
slot_name() {
    echo "${1##*-}" | tr '[:lower:]' '[:upper:]'
}

# Where LND keeps its data in this container, detected from where tls.cert lives.
lnd_dir() {
    local container_name=$1
    local candidate

    for candidate in ${LND_ROOT_CANDIDATES}; do
        if docker exec "${container_name}" test -f "${candidate}/tls.cert" 2>/dev/null; then
            echo "${candidate}"
            return 0
        fi
    done

    return 1
}

# Function to extract macaroon from container
extract_macaroon() {
    local container_name=$1
    local macaroon_path=$2
    
    echo -e "${YELLOW}Extracting macaroon from ${container_name}...${NC}" >&2
    
    if ! docker ps --format "table {{.Names}}" | grep -q "^${container_name}$"; then
        echo -e "${RED}Error: Container ${container_name} is not running${NC}"
        return 1
    fi
    
    # Extract macaroon and encode to hex. `od` rather than `xxd`, which the Polar app's LND
    # image does not ship.
    local macaroon_hex
    macaroon_hex=$(docker exec "${container_name}" od -An -v -tx1 "${macaroon_path}" 2>/dev/null | tr -d ' \n')

    # Guard against docker exec failures whose message lands on stdout and looks like a value.
    if [[ ! "$macaroon_hex" =~ ^[0-9a-f]+$ ]]; then
        echo -e "${RED}Error: Failed to extract macaroon from ${container_name} (${macaroon_path})${NC}"
        return 1
    fi

    echo ${macaroon_hex}
    return 0
}

extract_tls() {
    local container_name=$1
    local tls_path=$2

    echo -e "${YELLOW}Extracting TLS certificate from ${container_name}...${NC}" >&2

    if ! docker ps --format "table {{.Names}}" | grep -q "^${container_name}$"; then
        echo -e "${RED}Error: Container ${container_name} is not running${NC}"
        return 1
    fi

    # Extract TLS certificate and encode to hex
    local tls_hex
    tls_hex=$(docker exec "${container_name}" base64 "${tls_path}" 2>/dev/null | tr -d '\n')

    if [[ ! "$tls_hex" =~ ^[A-Za-z0-9+/=]+$ ]]; then
        echo -e "${RED}Error: Failed to extract TLS certificate from ${container_name}${NC}"
        return 1
    fi

    echo ${tls_hex}
    return 0
}

extract_pubkey() {
    local container_name=$1
    local lnd_root=$2

    if ! docker ps --format "table {{.Names}}" | grep -q "^${container_name}$"; then
        echo -e "${RED}Error: Container ${container_name} is not running${NC}"
        return 1
    fi

    local pubkey
    pubkey=$(docker exec "${container_name}" lncli -n regtest --lnddir "${lnd_root}" getinfo 2>/dev/null | grep identity_pubkey | cut -d'"' -f4)

    if [ -z "$pubkey" ]; then
        echo -e "${RED}Error: Failed to extract public key from ${container_name}${NC}"
        return 1
    fi

    echo ${pubkey}
    return 0
}

# Function to extract all LND data for a node
extract_lnd_node_data() {
    local node_name=$1
    local container_name=$2
    local host=$3

    local lnd_root
    if ! lnd_root=$(lnd_dir "${container_name}"); then
        echo -e "${RED}✗ ${container_name}: no LND directory found in ${LND_ROOT_CANDIDATES// /, } — is it running and initialized?${NC}"
        lnd_root="${LND_ROOT_CANDIDATES%% *}"
    fi

    echo "# ${node_name} LND Admin Macaroon" >> "${OUTPUT_FILE}"
    if macaroon=$(extract_macaroon "${container_name}" "${lnd_root}/data/chain/bitcoin/regtest/admin.macaroon"); then
        echo "${node_name}_MACAROON=\"${macaroon}\"" >> "${OUTPUT_FILE}"
        echo -e "${GREEN}✓ ${node_name} LND macaroon extracted${NC}"
    else
        echo "# ${node_name}_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
        echo -e "${RED}✗ Failed to extract ${node_name} LND macaroon${NC}"
    fi
    echo "" >> "${OUTPUT_FILE}"

    echo "# ${node_name} LND TLS Certificate" >> "${OUTPUT_FILE}"
    if tls_cert=$(extract_tls "${container_name}" "${lnd_root}/tls.cert"); then
        echo "${node_name}_LND_TLS_CERT=\"${tls_cert}\"" >> "${OUTPUT_FILE}"
        echo -e "${GREEN}✓ ${node_name} TLS certificate extracted${NC}"
    else
        echo "# ${node_name}_LND_TLS_CERT=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
        echo -e "${RED}✗ Failed to extract ${node_name} TLS certificate${NC}"
    fi
    echo "" >> "${OUTPUT_FILE}"

    echo "# ${node_name} LND Host and Pubkey" >> "${OUTPUT_FILE}"
    echo "${node_name}_HOST=\"${host}\"" >> "${OUTPUT_FILE}"
    if pubkey=$(extract_pubkey "${container_name}" "${lnd_root}"); then
        echo "${node_name}_PUBKEY=\"${pubkey}\"" >> "${OUTPUT_FILE}"
        echo -e "${GREEN}✓ ${node_name} pubkey extracted${NC}"
    else
        echo "# ${node_name}_PUBKEY=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
        echo -e "${RED}✗ Failed to extract ${node_name} pubkey${NC}"
    fi
    echo "" >> "${OUTPUT_FILE}"
}

# Function to extract all Loopd data for a node
extract_loopd_node_data() {
    local node_name=$1
    local container_name=$2
    local host=$3

    echo "# ${node_name} Loopd Admin Macaroon" >> "${OUTPUT_FILE}"
    if docker ps --format "table {{.Names}}" | grep -q "${container_name}"; then
        if loopd_macaroon=$(extract_macaroon "${container_name}" "${LOOP_ROOT}/regtest/loop.macaroon"); then
            echo "${node_name}_LOOPD_MACAROON=\"${loopd_macaroon}\"" >> "${OUTPUT_FILE}"
            echo -e "${GREEN}✓ ${node_name} Loopd macaroon extracted${NC}"
        else
            echo "# ${node_name}_LOOPD_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
            echo -e "${RED}✗ Failed to extract ${node_name} Loopd macaroon${NC}"
        fi
    else
        echo "# ${node_name}_LOOPD_MACAROON=\"<container_not_running>\"" >> "${OUTPUT_FILE}"
        echo -e "${YELLOW}⚠ ${node_name} Loopd container not running${NC}"
    fi
    echo "" >> "${OUTPUT_FILE}"

    echo "# ${node_name} Loopd TLS Certificate" >> "${OUTPUT_FILE}"
    if docker ps --format "table {{.Names}}" | grep -q "${container_name}"; then
        if loopd_tls=$(extract_tls "${container_name}" "${LOOP_ROOT}/regtest/tls.cert"); then
            echo "${node_name}_LOOPD_TLS_CERT=\"${loopd_tls}\"" >> "${OUTPUT_FILE}"
            echo -e "${GREEN}✓ ${node_name} Loopd TLS certificate extracted${NC}"
        else
            echo "# ${node_name}_LOOPD_TLS_CERT=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
            echo -e "${RED}✗ Failed to extract ${node_name} Loopd TLS certificate${NC}"
        fi
    else
        echo "# ${node_name}_LOOPD_TLS_CERT=\"<container_not_running>\"" >> "${OUTPUT_FILE}"
        echo -e "${YELLOW}⚠ ${node_name} Loopd container not running${NC}"
    fi
    echo "" >> "${OUTPUT_FILE}"

    echo "# ${node_name} Loopd Host" >> "${OUTPUT_FILE}"
    echo "${node_name}_LOOPD_HOST=\"${host}\"" >> "${OUTPUT_FILE}"
    echo "" >> "${OUTPUT_FILE}"
}

# Create output file
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_FILE="${SCRIPT_DIR}/../src/nodeguard-macaroons.env"
load_dotenv "${SCRIPT_DIR}/../.env"
echo -e "${YELLOW}Creating environment file: ${OUTPUT_FILE}${NC}"

cat > "${OUTPUT_FILE}" << 'EOF'
# NodeGuard Macaroon Environment Variables
# Generated automatically by extract-macaroons.sh

EOF

echo "" >> "${OUTPUT_FILE}"

echo "IS_DEV_ENVIRONMENT=true" >> "${OUTPUT_FILE}"

# Point NodeGuard at an externally-managed bitcoind when one was given. NodeGuard runs on the host,
# so it reaches the container through its published ports. These override the values baked into
# launchSettings.json / launch.json (DotNetEnv clobbers existing vars when loading the env file).
if [ -n "${BITCOIND_CONTAINER:-}" ]; then
    echo -e "${GREEN}=== Resolving external bitcoind (${BITCOIND_CONTAINER}) ===${NC}"
    external_rpc_port=$(published_port "${BITCOIND_CONTAINER}" "${BITCOIND_RPC_PORT_INTERNAL}")
    external_p2p_port=$(published_port "${BITCOIND_CONTAINER}" "${BITCOIND_P2P_PORT_INTERNAL}")

    if [ -z "${external_rpc_port}" ] || [ -z "${external_p2p_port}" ]; then
        echo -e "${RED}✗ Could not resolve published ports ${BITCOIND_RPC_PORT_INTERNAL}/${BITCOIND_P2P_PORT_INTERNAL} of ${BITCOIND_CONTAINER}${NC}"
        echo -e "${RED}  Is the container running and publishing them? (docker port ${BITCOIND_CONTAINER})${NC}"
        exit 1
    fi

    {
        echo "# External bitcoind: ${BITCOIND_CONTAINER}"
        echo "NBXPLORER_BTCRPCURL=\"http://127.0.0.1:${external_rpc_port}/\""
        echo "NBXPLORER_BTCNODEENDPOINT=\"127.0.0.1:${external_p2p_port}\""
        echo "NBXPLORER_BTCRPCUSER=\"${BITCOIND_RPCUSER:-polaruser}\""
        echo "NBXPLORER_BTCRPCPASSWORD=\"${BITCOIND_RPCPASSWORD:-polarpass}\""
        echo ""
    } >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ RPC on 127.0.0.1:${external_rpc_port}, P2P on 127.0.0.1:${external_p2p_port}${NC}"
fi

# Extract LND macaroons
echo -e "${GREEN}=== Extracting LND Macaroons ===${NC}"

if [ -n "${MANAGED_NODES:-}" ]; then
    # Externally-managed nodes: resolve each endpoint from the container's published gRPC port.
    IFS=', ' read -r -a managed_nodes <<< "${MANAGED_NODES}"
    managed_slots=()

    for container in "${managed_nodes[@]}"; do
        [ -z "${container}" ] && continue
        slot=$(slot_name "${container}")
        grpc_port=$(published_port "${container}" "${LND_GRPC_PORT}")

        if [ -z "${grpc_port}" ]; then
            echo -e "${RED}✗ ${container}: no published port for ${LND_GRPC_PORT}, skipping${NC}"
            continue
        fi

        extract_lnd_node_data "${slot}" "${container}" "localhost:${grpc_port}"
        managed_slots+=("${slot}")
    done

    echo "# Managed nodes extracted from: ${MANAGED_NODES}" >> "${OUTPUT_FILE}"
    echo "MANAGED_NODES=\"$(IFS=,; echo "${managed_slots[*]}")\"" >> "${OUTPUT_FILE}"
    echo "" >> "${OUTPUT_FILE}"

    # DbInitializer only seeds the alice/bob/carol slots (see src/Data/DbInitializer.cs), so any
    # other node is extracted but has to be added through the UI.
    for slot in "${managed_slots[@]}"; do
        case "${slot}" in
            ALICE|BOB|CAROL) ;;
            *) echo -e "${YELLOW}⚠ ${slot} was extracted but is not auto-seeded — add it in the NodeGuard UI (Nodes > Add node) using ${slot}_HOST/${slot}_PUBKEY/${slot}_MACAROON from ${OUTPUT_FILE}${NC}" ;;
        esac
    done
else
    # In-repo polar stack (docker compose --profile polar), fixed published ports.
    # Alice LND
    extract_lnd_node_data "ALICE" "polar-n1-alice" "localhost:10001"

    # Bob LND
    extract_lnd_node_data "BOB" "polar-n1-bob" "localhost:10002"

    # Carol LND
    extract_lnd_node_data "CAROL" "polar-n1-carol" "localhost:10003"
fi

# Extract Loopd macaroons
echo -e "${GREEN}=== Extracting Loopd Macaroons ===${NC}"

# Bob Loopd
extract_loopd_node_data "BOB" "nodeguard-loopd-bob-1" "localhost:11010"

# Carol Loopd
extract_loopd_node_data "CAROL" "nodeguard-loopd-carol-1" "localhost:11011"

echo -e "${GREEN}=== Extraction Complete ===${NC}"
echo -e "${YELLOW}Environment file created: ${OUTPUT_FILE}${NC}"