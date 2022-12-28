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