#!/bin/bash

# Script to extract admin macaroons from LND and Loopd containers
# This script generates environment variables for NodeGuard C# application

LND_ROOT="/root/.lnd"
LOOP_ROOT="/root/.loop"

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}=== NodeGuard Macaroon Extractor ===${NC}"

# Function to extract macaroon from container
extract_macaroon() {
    local container_name=$1
    local macaroon_path=$2
    
    echo -e "${YELLOW}Extracting macaroon from ${container_name}...${NC}" >&2
    
    if ! docker ps --format "table {{.Names}}" | grep -q "^${container_name}$"; then
        echo -e "${RED}Error: Container ${container_name} is not running${NC}"
        return 1
    fi
    
    # Extract macaroon and encode to hex
    local macaroon_hex
    macaroon_hex=$(docker exec "${container_name}" xxd -p -c 256 "${macaroon_path}" | tr -d '\n')
    
    if [ -z "$macaroon_hex" ]; then
        echo -e "${RED}Error: Failed to extract macaroon from ${container_name}${NC}"
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
    tls_hex=$(docker exec "${container_name}" base64 "${tls_path}" | tr -d '\n')

    if [ -z "$tls_hex" ]; then
        echo -e "${RED}Error: Failed to extract TLS certificate from ${container_name}${NC}"
        return 1
    fi

    echo ${tls_hex}
    return 0
}

extract_pubkey() {
    local container_name=$1

    if ! docker ps --format "table {{.Names}}" | grep -q "^${container_name}$"; then
        echo -e "${RED}Error: Container ${container_name} is not running${NC}"
        return 1
    fi

    local pubkey
    pubkey=$(docker exec "${container_name}" lncli -n regtest getinfo 2>/dev/null | grep identity_pubkey | cut -d'"' -f4)

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

    echo "# ${node_name} LND Admin Macaroon" >> "${OUTPUT_FILE}"
    if macaroon=$(extract_macaroon "${container_name}" "${LND_ROOT}/data/chain/bitcoin/regtest/admin.macaroon"); then
        echo "${node_name}_MACAROON=\"${macaroon}\"" >> "${OUTPUT_FILE}"
        echo -e "${GREEN}✓ ${node_name} LND macaroon extracted${NC}"
    else
        echo "# ${node_name}_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
        echo -e "${RED}✗ Failed to extract ${node_name} LND macaroon${NC}"
    fi
    echo "" >> "${OUTPUT_FILE}"

    echo "# ${node_name} LND TLS Certificate" >> "${OUTPUT_FILE}"
    if tls_cert=$(extract_tls "${container_name}" "${LND_ROOT}/tls.cert"); then
        echo "${node_name}_LND_TLS_CERT=\"${tls_cert}\"" >> "${OUTPUT_FILE}"
        echo -e "${GREEN}✓ ${node_name} TLS certificate extracted${NC}"
    else
        echo "# ${node_name}_LND_TLS_CERT=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
        echo -e "${RED}✗ Failed to extract ${node_name} TLS certificate${NC}"
    fi
    echo "" >> "${OUTPUT_FILE}"

    echo "# ${node_name} LND Host and Pubkey" >> "${OUTPUT_FILE}"
    echo "${node_name}_HOST=\"${host}\"" >> "${OUTPUT_FILE}"
    if pubkey=$(extract_pubkey "${container_name}"); then
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
echo -e "${YELLOW}Creating environment file: ${OUTPUT_FILE}${NC}"

cat > "${OUTPUT_FILE}" << 'EOF'
# NodeGuard Macaroon Environment Variables
# Generated automatically by extract-macaroons.sh

EOF

echo "" >> "${OUTPUT_FILE}"

echo "IS_DEV_ENVIRONMENT=true" >> "${OUTPUT_FILE}"

# Extract LND macaroons
echo -e "${GREEN}=== Extracting LND Macaroons ===${NC}"

# Alice LND
extract_lnd_node_data "ALICE" "polar-n1-alice" "localhost:10001"

# Bob LND
extract_lnd_node_data "BOB" "polar-n1-bob" "localhost:10002"

# Carol LND
extract_lnd_node_data "CAROL" "polar-n1-carol" "localhost:10003"

# Extract Loopd macaroons
echo -e "${GREEN}=== Extracting Loopd Macaroons ===${NC}"

# Bob Loopd
extract_loopd_node_data "BOB" "nodeguard-loopd-bob-1" "localhost:11010"

# Carol Loopd
extract_loopd_node_data "CAROL" "nodeguard-loopd-carol-1" "localhost:11011"

echo -e "${GREEN}=== Extraction Complete ===${NC}"
echo -e "${YELLOW}Environment file created: ${OUTPUT_FILE}${NC}"