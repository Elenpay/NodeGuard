# NodeGuard

Treasury management is a key component of the operational management of a lightning node. Having a lightning node with a lot of funds can become a cumbersome task when you need to operate at a scale with proper security mechanisms, however, lightning channels need liquidity from a Bitcoin treasury. In this context, a bitcoin treasury is a way of describing the source of funds required to enable the operations of a lightning service provider such as ours. We identified in this process that we would like to reduce the threat surface to the maximum extent possible, remember the mantra, not your keys, not your coins. And this is what we wanted, having a way to open lightning channels without real access to the private keys for node operators. This way, no technical members would have access to the private keys, and the lightning nodes on-chain funds in hot wallets would be the minimum as possible (this is subject to the lightning implementation you might use). In this use case, Node Operators want to sleep at night without having to worry about managing the private keys of a bitcoin treasury with a decent amount of funds. So based on the principle of least privilege (PoLP), we decided to split this responsibility by developing NodeGuard, a treasury management solution for lightning nodes. NodeGuard is a web application written in ASP.NET Core Blazor to provide an easy and intuitive UI for non-technical fellows who manage a Bitcoin treasury in lightning nodes.

Current features of NodeGuard are the following:

- Funding and opening of a lightning channel through read-only(no private key access) multisig wallets
- Asynchronous approval process based on Role-based Access Control (RBAC) and multisig wallets.
- Automatic sweeping of funds in lightning nodes to avoid having funds on the node hot wallets
- Channel creation interception with returning address to multisig wallets to avoid having funds on hot wallets
- In-browser notification systems for channel approvals
- Optional remote signing through AWS Lambda functions for channel funding transactions, separating the NodeGuard keys from the actual software
- Two-factor authentication

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


