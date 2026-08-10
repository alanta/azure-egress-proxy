FROM mcr.microsoft.com/devcontainers/dotnet:2.1.4-10.0-noble

ARG TARGETARCH
ARG ASPIRE_CLI_VERSION=13.4.6

ENV DEBIAN_FRONTEND=noninteractive

RUN rm -f /etc/apt/sources.list.d/yarn.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        apt-transport-https \
        ca-certificates \
        curl \
        git \
        gnupg \
        jq \
        lsb-release \
        shellcheck \
        unzip \
    && rm -rf /var/lib/apt/lists/*

RUN dotnet tool install --tool-path /usr/local/bin aspire.cli --version "$ASPIRE_CLI_VERSION" \
    && chmod -R a+rX /usr/local/bin/.store

RUN mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
        | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
    && chmod 644 /etc/apt/keyrings/microsoft.gpg \
    && case "$TARGETARCH" in \
        amd64) azure_arch=amd64 ;; \
        arm64) azure_arch=arm64 ;; \
        *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && echo "deb [arch=${azure_arch} signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/azure-cli/ $(lsb_release -cs) main" \
        > /etc/apt/sources.list.d/azure-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends azure-cli \
    && rm -rf /var/lib/apt/lists/*
