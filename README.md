# NodeGuard
![GitHub release (release name instead of tag name)](https://img.shields.io/github/v/release/Elenpay/NodeGuard)
![GitHub](https://img.shields.io/github/license/Elenpay/NodeGuard)
[![Unit tests](https://github.com/Elenpay/NodeGuard/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Elenpay/NodeGuard/actions/workflows/dotnet.yml)
[![Docker image build](https://github.com/Elenpay/NodeGuard/actions/workflows/docker.yaml/badge.svg)](https://github.com/Elenpay/NodeGuard/actions/workflows/docker.yaml)

<p align="center">
  <img src="nodeguard.png">
</p>
NodeGuard is an open-source technology stack developed to simplify treasury operations for lightning nodes, focusing on both Security and UX. It enables the management of lightning treasury funds, adhering to the principles of separation of duties and the principle of least privilege. These principles form the core of NodeGuard's functionality, aiming to eliminate the need for an internal node hot wallet and to separate key management from the actual node operators. At present, NodeGuard supports only LND. For a more detailed understanding, please watch the video below.

[![Watch the video](https://img.youtube.com/vi/qIQ5J0npj0c/maxresdefault.jpg)](https://youtu.be/qIQ5J0npj0c)

Current features of NodeGuard are the following:

- Asynchronous channel funding leveraging cold multisig wallets and hot wallets
- Multisig wallet creation and import (BIP39), only segwit for now
- Liquidity automation by settings rules in tandem with [NodeGuard liquidator](https://github.com/Elenpay/liquidator)
- Optional remote signing through [NodeGuard Remote Signer](https://github.com/Elenpay/Nodeguard-Remote-Signer) functions for channel funding transactions, separating the NodeGuard keys from the actual software
- Automatic sweeping of funds in lightning nodes to avoid having funds on the node hot wallets
- Channel management
- Channel creation interception with returning address to multisig wallets to avoid having funds on hot wallets
- Support for hardware wallets to sign the PSBTs for channel funding transactions
- Minimalistic in-browser wallet with [NodeGuard Companion](https://github.com/Elenpay/Nodeguards-Companion) to ease signing of transactions and wallet creation
- In-browser notification systems for channel approvals
- Two-factor authentication

# Contributing
Check [Contributing.md](CONTRIBUTING.md)

# Roadmap

TODO

# Dev environment quickstart

Run `tilt up` to run the whole infrastructure, then `just run` to run the project.

## Requirements

- VS Code / Visual Studio
- Docker desktop
- Dotnet SDK 6+
- Dotnet-ef global tool
- AWS Lambda function + AWS credentials for the Remote FundsManagerSigner, check [this](#trusted-coordinator-signing)
- Tilt
- Docker
- (Optional) [Polar lightning](https://lightningpolar.com/)
- (Optional) Go go 1.24.3 or later (for using the interactive commands in the .justfile)


## Migrations

This project uses NPGSQL(postgres) database provider for EfCore (ORM). You need to install dotnet-ef global tool
```
dotnet tool install -g dotnet-ef
```

- To update the database (create it & apply migrations) you shall do:
    ```
    cd src && dotnet ef database update
    ```
- To create a new migration
  ```
  cd src && dotnet ef migrations add changeInEntityExampleAddedNewField // This is an example
  ```
- To remove a non-applied migration (once a migration is applied, you have to drop the database to remove it)
    ```
    cd src && dotnet ef migrations remove
    ```


## Developing

## Running the infrastructure

### Using Tilt
1. Install [tilt](https://docs.tilt.dev/install.html)
2. Run `tilt up` on your terminal

### Using docker compose

1. If you want to run a lightweight version of the project use `docker compose --profile polar up -d` on your terminal. Add `--profile loop` and `--profile mempool` if you need to run them too

### Using polar

1. You can run the Polar network by importing the `devnetwork.zip` into it. Then you have to run `docker compose up -d` for the rest of the needed containers to start.

## Running the project

### Using the terminal

1. Run `just run` to build and run the project or `just watch` for hot reload on your terminal

### Using Visual Studio Code

1. Run the Debug NG launch setting on your terminal

### Using Rider/IntelliJ

1. You can run the task `NodeGuard local debug` that is in the `launchSettings.json` from any other IDE, just make sure you run first `./docker/extract-macaroons.sh` after starting the infrastructure so NodeGuard can get the latest macaroons

## Navigating NodeGuard

1. After completing the previous steps, navigate to `http://localhost:38080` to log in


# Security 
Check [Security.md](SECURITY.md)

# LICENSE
This project is licensed under AGPLv3.0. Check [LICENSE](LICENSE) for more information.