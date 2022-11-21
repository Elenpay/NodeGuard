# FundsManager

FundsManager is a web-app to control, audit and manage the governance of a Lightning Network node

## Getting started

1. Run polar regtest network with Polar, import devnetwork.polar.zip (in the root of this repo) and start it
2. Open FundsManager.sln with Visual Studio
3. Set startup project to docker-compose
4. Run

## Requirements

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

## Trusted coordinator signing
The fundsmanager has two modes of signing transactions as as Trusted coordinator (it is mandatory for security reasons, since the FM relies on SIGHASH_NONE inputs from finance managers):
1. Embedded (legacy) signing withing the fundsmanager, less secure but easier to manage
2. Use a remote signing function (AWS Lambda function with public URL) to sign with a AWS KMS encrypted seedphrase that only the function can decrypt. Bear in mind that the AWS KMS symmetric encryption private key is never exposed and it is managed by AWS KMS.

To enable mode #1 set env var as follows `ENABLE_REMOTE_FM_SIGNER = false`, otherwise in case you want mode #2 you need a lambda function deployed with the C# Project in `src/signer/FundsManagerSigner` with a public function URL and a AWS KMS Symmetric encryption key. Then set up the following env vars as in this example (json-like):
```
"ENABLE_REMOTE_FM_SIGNER": "true",
"AWS_ACCESS_KEY_ID": "********",
"AWS_SECRET_ACCESS_KEY": "********",
"AWS_REGION": "eu-west-1",
"AWS_KMS_KEY_ID": "mrk-cec3e3ef59bc4616a6f44da60bfea0ba", 
"FM_SIGNER_ENDPOINT": "https://*.lambda-url.eu-west-1.on.aws/"
```
They are detailed as follows:
- AWS_ACCESS_KEY_ID:  IAM-based user account id used to auth against AWS lambda
- AWS_SECRET_ACCESS_KEY: IAM-based user secret
- AWS_REGION: the region code of the AWS deployed lambda function
- FM_SIGNER_ENDPOINT: AWS Function url endpoint, check https://docs.aws.amazon.com/lambda/latest/dg/lambda-urls.html

### Invoking the lambda function

The key aspect when invoking the lambda function is to use the AWS API Gateway format for payloads, check [AWS Function URL invocation basics](https://docs.aws.amazon.com/lambda/latest/dg/urls-invocation.html).


Example input to AWS lambda function:
```json
{
  "Version": null,
  "RouteKey": null,
  "RawPath": null,
  "RawQueryString": null,
  "Cookies": null,
  "Headers": null,
  "QueryStringParameters": null,
  "RequestContext": null,
  "Body": "{\"Psbt\":\"cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15\\u002B6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP\\u002BhqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po\\u002BBAlGvFeBbuLfqwYlbP19H/\\u002B/s2DIaAu8iKY\\u002BJ0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6\\u002BzUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn\\u002B/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz\\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgKsJYoVeZWSHLhJIIELCGqDZXBWF2JcYFgYUbTSg31gYCIAbh5LXC9mmOKmqjB3kW3rgBbHrht4B3Vz5jDXmrS\\u002Bn7AgEDBAIAAAABBWlSIQLYVMVgz\\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz\\u002BbATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI\\u002BWNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF\\u002BN0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==\",\"EnforcedSighash\":1,\"Network\":\"Regtest\"}",
  "PathParameters": null,
  "IsBase64Encoded": false,
  "StageVariables": null
}
```
Request input body fields:
- Psbt: The base64-encoded PSBT to sign
- EnforcedSighash: used to enforce a SIGHASH mode signing for all the inputs, 1 means SIGHASH_ALL, check NBitcoin enums to match other sighash types.
- Network: Bitcoin network, this is case-insensitive as long the values are either one of the following `Mainnet, Regtest, Testnet`

Example output from the lambda function:

```json
{
  "statusCode": 200,
  "body": "{\"Psbt\":\"cHNidP8BAF4BAAAAAWAvqvtTSjdcNjNuK8YKWQg7RM1S8LFDdIXg3KU34l6/AQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15\\u002B6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP\\u002BhqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po\\u002BBAlGvFeBbuLfqwYlbP19H/\\u002B/s2DIaAu8iKY\\u002BJ0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6\\u002BzUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn\\u002B/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQBAwQBAAAAAQVpUiEC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEhAwJn/wsRl0hvcYj5Y3Bv3uQlxZ57pBZ9KSeuEPVNmjS/IQMaU3fyWsF\\u002BN0FpN8hSusDj6bESvd9YR509kdgWMLKLj1OuIgIC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLFHMEQCICrCWKFXmVkhy4SSCBCwhqg2VwVhdiXGBYGFG00oN9YGAiAG4eS1wvZpjipqowd5Ft64AWx64beAd1c\\u002BYw15q0vp\\u002BwIiAgMaU3fyWsF\\u002BN0FpN8hSusDj6bESvd9YR509kdgWMLKLj0cwRAIgU0100AuYgFliCrcGHwN4nB5ZIPSTbGlFEyjuCccDgxICIBf3Zeqc\\u002B7g49r\\u002BnIYw7tFpo7Jt6RasMja2X3RJuy9Y/ASIGAthUxWDP5sBOC\\u002BtENBtqeUBJVe2JTA\\u002B33IKCRB\\u002B/aSyxGB/M5N4wAACAAQAAgAEAAIAAAAAAAAAAACIGAwJn/wsRl0hvcYj5Y3Bv3uQlxZ57pBZ9KSeuEPVNmjS/GGDzoLMwAACAAQAAgAEAAIAAAAAAAAAAACIGAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPGO0CEMgwAACAAQAAgAEAAIAAAAAAAAAAAAAA\"}",
  "isBase64Encoded": false
}
```

Request output body fields:
- Psbt: The base64-encoded signed PSBT

### Encrypted Seedphrase generation
Right now, the easiest way to encrypt a wallet seedphrase (AKA Mnemomnic) is to use the function `EncryptSeedphrase` in the `Function.cs` class in the Remote signer by invoking a unit test to generate an encrypted seedphrase which is in  the `src/signer/FundsManagerSigner/FunctionTest.cs` named `GenerateEncryptedSeedTest`. Take into account that you must use [AWS SDK Credentials for .NET](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/net-dg-config-creds.html) to call AWS KMS. 

### Setting the function main config
The lambda function uses environment variables as a key-value dictionary for configuration of the different wallets that can be used to sign, the dictionary keys are the master fingerprints of the different wallets while the value of the keys are the configuration of the lambda function.

The configuration has two fields:
* EncryptedSeedphrase: The encrypted seedphrase as explained [above](#encrypted-seedphrase-generation)
* AwsKmsKeyId: Symmetric key generated by AWS KMS which decrypts the seedphrase

Example (json-like structure of key-value, `ed0210c8` is the master fingerprint of the wallet):
```json
{
"ed0210c8": {
  "EncryptedSeedphrase": "AQICAHheBtxW+2iTBvvhvmRXaxaScHh6up1/VWCRSMlopexrdwE1C/ylXBL5pmjJ3P/UG7XnAAABBzCCAQMGCSqGSIb3DQEHBqCB9TCB8gIBADCB7AYJKoZIhvcNAQcBMB4GCWCGSAFlAwQBLjARBAxPlkxPX65p7aRcXykCARCAgb4En2Bb/nWQ6m4i3JDP+KGjaGDAVF4LR6+2Ljl7orp6pfZbCCxK6e89OBpJWi7elQM670vD/SWkYSZ9MUWUshU8n7NyBJZuZgBhtaH6j6yDhgHtBv7cwJngv0d72QEaTrH2YqLCVuoddEKEpB13ezfkf56230QD134kcJze4fITQGA6sXxQ0x+WjKOeYltpB+Shk4+kaNja42ZM0MMjyrMOmQtXCkgdoTUVi6twiqU+qr8mQEEq0aNdZzlLCI/v",
  "AwsKmsKeyId": "mrk-cec3e3ef59bc4616a6f44da60bfea0ba"
}}
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


