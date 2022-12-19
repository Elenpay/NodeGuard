# AWS Lambda Empty Docker Image Function Project

This starter project consists of:
* Function.cs - Class file containing a class with a single function handler method
* Dockerfile - Used with the `docker build` command to build the docker image
* aws-lambda-tools-defaults.json - default argument settings for use within Visual Studio and command line deployment tools for AWS

You may also have a test project depending on the options selected.

## Packaging as a Docker image.

This project is configured to package the Lambda function as a Docker image. The default configuration for the project and the Dockerfile is to build 
the .NET project on the host machine and then execute the `docker build` command which copies the .NET build artifacts from the host machine into 
the Docker image. 

The `--docker-host-build-output-dir` switch, which is set in the `aws-lambda-tools-defaults.json`, triggers the 
AWS .NET Lambda tooling to build the .NET project into the directory indicated by `--docker-host-build-output-dir`. The Dockerfile 
has a **COPY** command which copies the value from the directory pointed to by `--docker-host-build-output-dir` to the `/var/task` directory inside of the 
image.

Alternatively the Docker file could be written to use [multi-stage](https://docs.docker.com/develop/develop-images/multistage-build/) builds and 
have the .NET project built inside the container. Below is an example of building the .NET project inside the image.

```dockerfile
FROM public.ecr.aws/lambda/dotnet:6 AS base

FROM mcr.microsoft.com/dotnet/sdk:6.0-bullseye-slim as build
WORKDIR /src
COPY ["FundsManagerSigner.csproj", "FundsManagerSigner/"]
RUN dotnet restore "FundsManagerSigner/FundsManagerSigner.csproj"

WORKDIR "/src/FundsManagerSigner"
COPY . .
RUN dotnet build "FundsManagerSigner.csproj" --configuration Release --output /app/build

FROM build AS publish
RUN dotnet publish "FundsManagerSigner.csproj" \
            --configuration Release \ 
            --runtime linux-x64 \
            --self-contained false \ 
            --output /app/publish \
            -p:PublishReadyToRun=true  

FROM base AS final
WORKDIR /var/task
COPY --from=publish /app/publish .
```

When building the .NET project inside the image you must be sure to copy all of the class libraries the .NET Lambda project is depending on 
as well before the `dotnet build` step. The final published artifacts of the .NET project must be copied to the `/var/task` directory. 
The `--docker-host-build-output-dir` switch can also be removed from the `aws-lambda-tools-defaults.json` to avoid the 
.NET project from being built on the host machine before calling `docker build`.



## Here are some steps to follow from Visual Studio:

To deploy your function to AWS Lambda, right click the project in Solution Explorer and select *Publish to AWS Lambda*.

To view your deployed function open its Function View window by double-clicking the function name shown beneath the AWS Lambda node in the AWS Explorer tree.

To perform testing against your deployed function use the Test Invoke tab in the opened Function View window.

To configure event sources for your deployed function, for example to have your function invoked when an object is created in an Amazon S3 bucket, use the Event Sources tab in the opened Function View window.

To update the runtime configuration of your deployed function use the Configuration tab in the opened Function View window.

To view execution logs of invocations of your function use the Logs tab in the opened Function View window.

## Here are some steps to follow to get started from the command line:

You can deploy your application using the [Amazon.Lambda.Tools Global Tool](https://github.com/aws/aws-extensions-for-dotnet-cli#aws-lambda-amazonlambdatools) from the command line. Lambda function packaged as a Docker image require version 5.0.0 or later.

Install Amazon.Lambda.Tools Global Tools if not already installed.
```
dotnet tool install -g Amazon.Lambda.Tools
```

If already installed check if new version is available.
```
dotnet tool update -g Amazon.Lambda.Tools
```

Execute unit tests
```
cd "FundsManagerSigner/test/FundsManagerSigner.Tests"
dotnet test
```

Deploy function to AWS Lambda
```
cd "FundsManagerSigner/src/FundsManagerSigner"
dotnet lambda deploy-function
```

## Using CI + AWS ECR

This repo now provides CI to build this image (if changes overt this repo).
The image can lately be pulled and manually pushed to AWS ECR:
```
docker login registry.gitlab.com/clovrlabs/lightningnetwork/
docker pull registry.gitlab.com/clovrlabs/lightningnetwork/fundsigner:develop

docker tag registry.gitlab.com/clovrlabs/lightningnetwork/fundsigner:develop 839166930136.dkr.ecr.eu-central-1.amazonaws.com/fundsmanagersigner:latest

aws ecr get-login-password --region eu-central-1 | docker login --username AWS --password-stdin 839166930136.dkr.ecr.eu-central-1.amazonaws.com

docker push 839166930136.dkr.ecr.eu-central-1.amazonaws.com/fundsmanagersigner:latest
```