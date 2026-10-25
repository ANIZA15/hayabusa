# syntax=docker/dockerfile:1

# =============================================================================
# Stage 1: Build & Publish (Supports both JIT Release and NativeAOT across .NET 9 / .NET 10+)
# =============================================================================
ARG DOTNET_VERSION=9.0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG PUBLISH_AOT=false
ARG TARGET_FRAMEWORK=net9.0

RUN apt-get update && apt-get install -y --no-install-recommends \
    clang \
    zlib1g-dev \
    liburing-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy project configuration and license notice files first for layer caching
COPY global.json* Directory.Build.props* Directory.Packages.props* .editorconfig* ./
COPY THIRD-PARTY-NOTICES.md LICENSE* ./
COPY src/ ./src/
COPY samples/ ./samples/

RUN dotnet restore "./samples/Hayabusa.SampleServer/Hayabusa.SampleServer.csproj"

RUN dotnet publish "./samples/Hayabusa.SampleServer/Hayabusa.SampleServer.csproj" \
    -c "${BUILD_CONFIGURATION}" \
    -f "${TARGET_FRAMEWORK}" \
    -p:PublishAot="${PUBLISH_AOT}" \
    -o /app/publish \
    --no-restore

# =============================================================================
# Stage 2: Production Runtime Image (Minimal Ubuntu 24.04 + liburing2)
# =============================================================================
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}-noble AS runtime

RUN apt-get update && apt-get install -y --no-install-recommends \
    liburing2 \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Include third-party MIT license notices (Cysharp OSS compliance)
COPY --from=build /src/THIRD-PARTY-NOTICES.md /app/THIRD-PARTY-NOTICES.md
COPY --from=build /app/publish ./

# Default Hayabusa Server environment configuration (overridable via docker run -e / compose)
ENV HAYABUSA_ROLE="both" \
    HAYABUSA_BIND_ADDRESS="0.0.0.0" \
    HAYABUSA_HTTP_PORT="5000" \
    HAYABUSA_RPC_PORT="5001" \
    HAYABUSA_WORKER_THREADS="0" \
    HAYABUSA_TICK_RATE="60" \
    HAYABUSA_TRANSPORT="auto" \
    DOTNET_ServerGC="1" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1"

EXPOSE 5000/tcp
EXPOSE 5001/tcp

USER ubuntu
ENTRYPOINT ["dotnet", "Hayabusa.SampleServer.dll"]
