#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["src/FundsManager.csproj", "."]
RUN dotnet restore "./FundsManager.csproj"
COPY . .
WORKDIR /src/src
RUN ls -l
RUN dotnet build "FundsManager.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FundsManager.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY src/Migrations/*.sql Migrations/
ENTRYPOINT ["dotnet", "FundsManager.dll"]