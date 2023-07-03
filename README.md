# NodeGuard
![GitHub release (release name instead of tag name)](https://img.shields.io/github/v/release/Elenpay/NodeGuard)
![GitHub](https://img.shields.io/github/license/Elenpay/NodeGuard)
[![Unit tests](https://github.com/Elenpay/NodeGuard/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Elenpay/NodeGuard/actions/workflows/dotnet.yml)
[![Docker image build](https://github.com/Elenpay/NodeGuard/actions/workflows/docker.yaml/badge.svg)](https://github.com/Elenpay/NodeGuard/actions/workflows/docker.yaml)

<p align="center">
  <img src="nodeguard.png">
</p>
NodeGuard is a open source tech stack designed to simplify treasury ops in terms of Security and UX for lightning nodes. Currently only LND is supported. NodeGuards allows to manage a lightning treasury funds based on separation of duties and principle of least privilege principles. Watch the video below to learn more about it!

[![Watch the video](https://img.youtube.com/vi/qIQ5J0npj0c/maxresdefault.jpg)](https://youtu.be/qIQ5J0npj0c)

Current features of NodeGuard are the following:

- Funding and opening of a lightning channels using cold stored multisig wallets. Hot wallets are also supported.
- Asynchronous channel funding leveraging multisig wallets
- Automatic sweeping of funds in lightning nodes to avoid having funds on the node hot wallets
- Channel creation interception with returning address to multisig wallets to avoid having funds on hot wallets
- Liquidity automation by settings rules in tandem with [NodeGuard liquidator](https://github.com/Elenpay/liquidator)
- In-browser notification systems for channel approvals
- Optional remote signing through [NodeGuard Remote Signer](https://github.com/Elenpay/Nodeguard-Remote-Signer) functions for channel funding transactions, separating the NodeGuard keys from the actual software
- Minimalistic in-browser wallet, [NodeGuard Companion](https://github.com/Elenpay/Nodeguard-Remote-Signer) to ease signing of transactions and wallet creation
- Two-factor authentication

# Contributing
Check [Contributing.md](CONTRIBUTING.md)

# Roadmap

TODO

# Dev environment quickstart

1. Run polar regtest network with Polar, import devnetwork.polar.zip (in the root of this repo) and start it
2. Open FundsManager.sln with Visual Studio or your favourite IDE/EDITOR
3. Set startup project to docker-compose
4. Run

##Requirements

- VS Code / Visual Studio
- Docker desktop
- Dotnet SDK 6+
- Dotnet-ef global tool
- [Polar lightning](https://lightningpolar.com/)
- AWS Lambda function + AWS credentials for the Remote FundsManagerSigner, check [this](#trusted-coordinator-signing)


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

### Visual Studio
Launch the FundsManager Docker VS task
Launch The FundsManager VS task

### Rider/IntelliJ
Import and start `devnetwork.polar.zip` in polar
Launch the FundsManager Docker NOVS task
Launch The FundsManager NOVS task

### Visual Studio Code
Import and start `devnetwork.polar.zip` in polar
Start docker compose from terminal (see below)
Then, start the vscode launch configuration `Launch against running docker-compose env (DEV)`
Navigate to http://localhost:38080/

### Starting docker compose from terminal
Start all the dependencies in docker-compose by running:
```bash
cd docker
docker-compose -f docker-compose.dev-novs.yml up -d
```

# Security 
Check [Security.md](SECURITY.md)

# LICENSE
This project is Licensed under AGPLv3.0. Check [LICENSE](LICENSE) for more information.