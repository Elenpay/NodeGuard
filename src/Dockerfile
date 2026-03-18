#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/NodeGuard.csproj", "."]
RUN dotnet restore "./NodeGuard.csproj"
COPY . .
WORKDIR /src/src
RUN ls -l
RUN dotnet build "NodeGuard.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "NodeGuard.csproj" -c Release -o /app/publish

FROM base AS final

# Run as non-root user (dapr nonroot UID/GID)
RUN groupadd --gid 65532 nonroot && \
    useradd --uid 65532 --gid 65532 --no-create-home --shell /usr/sbin/nologin nonroot

WORKDIR /app
COPY --from=publish --chown=65532:65532 /app/publish .
COPY --chown=65532:65532 src/Migrations/*.sql Migrations/

USER 65532:65532
ENTRYPOINT ["dotnet", "NodeGuard.dll"]