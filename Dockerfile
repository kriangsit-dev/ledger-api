# syntax=docker/dockerfile:1

# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore using only the files that can affect it. Editing a .cs file then leaves this layer
# cached, which is the difference between a 20-second and a two-minute deploy.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Ledger.Domain/Ledger.Domain.csproj src/Ledger.Domain/
COPY src/Ledger.Infrastructure/Ledger.Infrastructure.csproj src/Ledger.Infrastructure/
COPY src/Ledger.Api/Ledger.Api.csproj src/Ledger.Api/
RUN dotnet restore src/Ledger.Api/Ledger.Api.csproj

COPY src/ src/
RUN dotnet publish src/Ledger.Api/Ledger.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Alpine carries no ICU. The solution already builds with InvariantGlobalization, so this is a
# statement of intent rather than a workaround.
ENV DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

COPY --from=build /app ./

# The aspnet images ship a non-root user; running as root in a container is a habit worth not having.
USER $APP_UID

ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
