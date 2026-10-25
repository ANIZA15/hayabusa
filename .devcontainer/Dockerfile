# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0-noble

ARG DEBIAN_FRONTEND=noninteractive
ARG USERNAME=ubuntu

# NativeAOT, Linux io_uring (liburing-dev), lizard (cyclomatic complexity), and network/profiling tools
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    clang \
    zlib1g-dev \
    liburing-dev \
    pkg-config \
    git \
    curl \
    ca-certificates \
    jq \
    iproute2 \
    procps \
    python3 \
    python3-pip \
    && pip3 install --no-cache-dir --break-system-packages lizard \
    && rm -rf /var/lib/apt/lists/*

# Ensure persistent directories for VS Code / Cursor extensions and NuGet cache owned by non-root user
RUN mkdir -p /home/${USERNAME}/.vscode-server/extensions \
             /home/${USERNAME}/.cursor-server/extensions \
             /home/${USERNAME}/.nuget/packages \
             /commandhistory \
    && touch /commandhistory/.bash_history \
    && chown -R ${USERNAME}:${USERNAME} /home/${USERNAME} /commandhistory

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    HISTFILE=/commandhistory/.bash_history

USER ${USERNAME}
WORKDIR /workspace
