#!/bin/bash

# Script to extract admin macaroons from LND and Loop containers
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
echo "# Alice LND Admin Macaroon" >> "${OUTPUT_FILE}"
if ALICE_MACAROON=$(extract_macaroon "polar-n1-alice" "${LND_ROOT}/data/chain/bitcoin/regtest/admin.macaroon"); then
    echo "ALICE_MACAROON=\"${ALICE_MACAROON}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Alice LND macaroon extracted${NC}"
else
    echo "# ALICE_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Alice LND macaroon${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Bob LND
echo "# Bob LND Admin Macaroon" >> "${OUTPUT_FILE}"
if BOB_MACAROON=$(extract_macaroon "polar-n1-bob" "${LND_ROOT}/data/chain/bitcoin/regtest/admin.macaroon"); then
    echo "BOB_MACAROON=\"${BOB_MACAROON}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Bob LND macaroon extracted${NC}"
else
    echo "# BOB_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Bob LND macaroon${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Carol LND
echo "# Carol LND Admin Macaroon" >> "${OUTPUT_FILE}"
if CAROL_MACAROON=$(extract_macaroon "polar-n1-carol" "${LND_ROOT}/data/chain/bitcoin/regtest/admin.macaroon"); then
    echo "CAROL_MACAROON=\"${CAROL_MACAROON}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Carol LND macaroon extracted${NC}"
else
    echo "# CAROL_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Carol LND macaroon${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Extract Loop macaroons
echo -e "${GREEN}=== Extracting Loop Macaroons ===${NC}"

# Bob Loop
echo "# Bob Loop Admin Macaroon" >> "${OUTPUT_FILE}"
if docker ps --format "table {{.Names}}" | grep -q "docker-loopd-bob-1"; then
    if BOB_LOOP_MACAROON=$(extract_macaroon "docker-loopd-bob-1" "${LOOP_ROOT}/regtest/loop.macaroon"); then
        echo "BOB_LOOP_MACAROON=\"${BOB_LOOP_MACAROON}\"" >> "${OUTPUT_FILE}"
        echo -e "${GREEN}✓ Bob Loop macaroon extracted${NC}"
    else
        echo "# BOB_LOOP_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
        echo -e "${RED}✗ Failed to extract Bob Loop macaroon${NC}"
    fi
else
    echo "# BOB_LOOP_MACAROON=\"<container_not_running>\"" >> "${OUTPUT_FILE}"
    echo -e "${YELLOW}⚠ Bob Loop container not running${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Carol Loop
echo "# Carol Loop Admin Macaroon" >> "${OUTPUT_FILE}"
if docker ps --format "table {{.Names}}" | grep -q "docker-loopd-carol-1"; then
    if CAROL_LOOP_MACAROON=$(extract_macaroon "docker-loopd-carol-1" "${LOOP_ROOT}/regtest/loop.macaroon"); then
        echo "CAROL_LOOP_MACAROON=\"${CAROL_LOOP_MACAROON}\"" >> "${OUTPUT_FILE}"
        echo -e "${GREEN}✓ Carol Loop macaroon extracted${NC}"
    else
        echo "# CAROL_LOOP_MACAROON=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
        echo -e "${RED}✗ Failed to extract Carol Loop macaroon${NC}"
    fi
else
    echo "# CAROL_LOOP_MACAROON=\"<container_not_running>\"" >> "${OUTPUT_FILE}"
    echo -e "${YELLOW}⚠ Carol Loop container not running${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Add TLS certificates as well
echo -e "${GREEN}=== Extracting TLS Certificates ===${NC}"

# Alice TLS
echo "# Alice LND TLS Certificate" >> "${OUTPUT_FILE}"
if ALICE_TLS=$(extract_tls "polar-n1-alice" "${LND_ROOT}/tls.cert"); then
    echo "ALICE_LND_TLS_CERT=\"${ALICE_TLS}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Alice TLS certificate extracted${NC}"
else
    echo "# ALICE_LND_TLS_CERT=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Alice TLS certificate${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Bob TLS
echo "# Bob LND TLS Certificate" >> "${OUTPUT_FILE}"
if BOB_TLS=$(extract_tls "polar-n1-bob" "${LND_ROOT}/tls.cert"); then
    echo "BOB_LND_TLS_CERT=\"${BOB_TLS}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Bob TLS certificate extracted${NC}"
else
    echo "# BOB_LND_TLS_CERT=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Bob TLS certificate${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Carol TLS
echo "# Carol LND TLS Certificate" >> "${OUTPUT_FILE}"
if CAROL_TLS=$(extract_tls "polar-n1-carol" "${LND_ROOT}/tls.cert"); then
    echo "CAROL_LND_TLS_CERT=\"${CAROL_TLS}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Carol TLS certificate extracted${NC}"
else
    echo "# CAROL_LND_TLS_CERT=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Carol TLS certificate${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Add host and pubkey information
echo -e "${GREEN}=== Adding Host and Pubkey Information ===${NC}"

# Alice
echo "# Alice LND Host and Pubkey" >> "${OUTPUT_FILE}"
echo "ALICE_HOST=\"localhost:10001\"" >> "${OUTPUT_FILE}"
if ALICE_PUBKEY=$(extract_pubkey "polar-n1-alice"); then
    echo "ALICE_PUBKEY=\"${ALICE_PUBKEY}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Alice pubkey extracted${NC}"
else
    echo "# ALICE_PUBKEY=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Alice pubkey${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Bob
echo "# Bob LND Host and Pubkey" >> "${OUTPUT_FILE}"
echo "BOB_HOST=\"localhost:10002\"" >> "${OUTPUT_FILE}"
echo "BOB_LOOP=\"localhost:11010\"" >> "${OUTPUT_FILE}"
if BOB_PUBKEY=$(extract_pubkey "polar-n1-bob"); then
    echo "BOB_PUBKEY=\"${BOB_PUBKEY}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Bob pubkey extracted${NC}"
else
    echo "# BOB_PUBKEY=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Bob pubkey${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

# Carol
echo "# Carol LND Host and Pubkey" >> "${OUTPUT_FILE}"
echo "CAROL_HOST=\"localhost:10003\"" >> "${OUTPUT_FILE}"
echo "CAROL_LOOP=\"localhost:11011\"" >> "${OUTPUT_FILE}"
if CAROL_PUBKEY=$(extract_pubkey "polar-n1-carol"); then
    echo "CAROL_PUBKEY=\"${CAROL_PUBKEY}\"" >> "${OUTPUT_FILE}"
    echo -e "${GREEN}✓ Carol pubkey extracted${NC}"
else
    echo "# CAROL_PUBKEY=\"<failed_to_extract>\"" >> "${OUTPUT_FILE}"
    echo -e "${RED}✗ Failed to extract Carol pubkey${NC}"
fi

echo "" >> "${OUTPUT_FILE}"

echo -e "${GREEN}=== Extraction Complete ===${NC}"
echo -e "${YELLOW}Environment file created: ${OUTPUT_FILE}${NC}"