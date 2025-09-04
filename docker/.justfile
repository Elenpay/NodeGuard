## Bitcoin and lnd related commands ##


# Interactive command runner
interactive:
    #!/usr/bin/env bash
    
    # Select program type
    program=$(gum choose "bitcoind" "lnd")
    
    if [ "$program" = "bitcoind" ]; then
        # Bitcoin CLI commands
        command=$(gum choose "getinfo" "generate" "generateemptyblock" "sendtoaddress" "getbalance" "getnewaddress" "getblockcount" "custom")
        
        case $command in
            "getinfo")
                just bitcoin-cli getblockchaininfo
                ;;
            "generate")
                blocks=$(gum input --placeholder "Number of blocks to generate")
                just generate $blocks
                ;;
            "generateemptyblock")
                address=$(just bitcoin-cli getnewaddress)
                just bitcoin-cli generateblock $address "[]"
                ;;
            "sendtoaddress")
                address=$(gum input --placeholder "Destination address")
                amount=$(gum input --placeholder "Amount to send")
                just sendtoaddress $address $amount
                ;;
            "getbalance")
                just bitcoin-cli getbalance
                ;;
            "getnewaddress")
                just bitcoin-cli getnewaddress
                ;;
            "getblockcount")
                just bitcoin-cli getblockcount
                ;;
            "custom")
                cmd=$(gum input --placeholder "Enter custom bitcoin-cli command")
                just bitcoin-cli $cmd
                ;;
        esac
    
    elif [ "$program" = "lnd" ]; then
        # LND commands
        command=$(gum choose "getinfo" "addinvoice" "payinvoice" "listchannels" "openchannel" "closechannel" "walletbalance" "channelbalance" "newaddress" "custom")
        
        case $command in
            "getinfo")
                just getinfo
                ;;
            "addinvoice")
                amount=$(gum input --placeholder "Invoice amount in satoshis")
                just addinvoice $amount
                ;;
            "payinvoice")
                invoice=$(gum input --placeholder "Payment request/invoice")
                just payinvoice $invoice
                ;;
            "listchannels")
                just lncli listchannels
                ;;
            "openchannel")
                pubkey=$(gum input --placeholder "Node public key")
                amount=$(gum input --placeholder "Channel amount in satoshis")
                just lncli openchannel --node_key $pubkey --local_amt $amount
                ;;
            "closechannel")
                funding_txid=$(gum input --placeholder "Funding transaction ID")
                output_index=$(gum input --placeholder "Output index")
                just lncli closechannel --funding_txid $funding_txid --output_index $output_index
                ;;
            "walletbalance")
                just lncli walletbalance
                ;;
            "channelbalance")
                just lncli channelbalance
                ;;
            "newaddress")
                just lncli newaddress p2wkh
                ;;
            "custom")
                cmd=$(gum input --placeholder "Enter custom lncli command")
                just lncli $cmd
                ;;
        esac
    fi


# sets up nodes
setup-nodes:
    docker compose --profile polar up e2e_setup

# Run command within bitcoind container
bitcoin-cli *cmd:
    docker exec --user bitcoin polar-n1-backend bitcoin-cli -regtest {{cmd}}

# Send to address with fee rate
sendtoaddress address amount:
   just bitcoin-cli -named sendtoaddress address={{address}} amount={{amount}} fee_rate=25

# Generate blocks for bitcoin
generate blocks:
    just bitcoin-cli -generate {{blocks}}

generateemptyblock:
    #!/bin/bash
    address=$(just bitcoin-cli getnewaddress)
    just bitcoin-cli generateblock $address "[]"

lncli *cmd:
    #!/usr/bin/env bash
    node=$(gum choose alice bob carol)
    docker exec polar-n1-$node lncli --network regtest -lnddir /home/lnd/.lnd/ {{cmd}}

lnd *cmd:
    just lncli  {{cmd}}

getinfo:
    just lncli getinfo
    
addinvoice amount:
    just lncli addinvoice --amt {{amount}}

payinvoice invoice:
    just lncli payinvoice {{invoice}} -f

