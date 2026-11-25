# --platform=$BUILDPLATFORM: run restore/publish natively on the build host even when the
# target is another arch (arm64 CI job). The publish output is framework-dependent IL
# (UseAppHost=false), so it is architecture-neutral; only the tiny runtime stage below is
# emulated. Running the .NET SDK itself under QEMU is unsupported, slow and flaky.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG MATGATE_VERSION=

COPY Matgate/Matgate.csproj Matgate/
COPY Mat.Remote/Mat.Remote.csproj Mat.Remote/
RUN dotnet restore Matgate/Matgate.csproj

COPY Mat.Remote/ Mat.Remote/
COPY Matgate/ Matgate/
RUN if [ -n "$MATGATE_VERSION" ]; then \
      dotnet publish Matgate/Matgate.csproj -c Release -o /app/publish /p:UseAppHost=false /p:Version=$MATGATE_VERSION /p:InformationalVersion=$MATGATE_VERSION; \
    else \
      dotnet publish Matgate/Matgate.csproj -c Release -o /app/publish /p:UseAppHost=false; \
    fi

# WireGuard tunnels (see Services/TunnelService.cs): wireproxy, a WireGuard client that runs as an ordinary
# program - no kernel module, no NET_ADMIN, no TUN device - and offers the network behind the tunnel as a
# SOCKS5 proxy on 127.0.0.1. ISC licence. Pinned to one release and checked against its published SHA-256.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS wireproxy
ARG TARGETARCH
RUN set -eu; \
    arch="${TARGETARCH:-amd64}"; \
    case "$arch" in \
      amd64) sum=e88c1d090740373fc606c1bafd81d9a5eadc642cce5667616e20e9d7a444f51c ;; \
      arm64) sum=370e00bd2167960d1ecd1c3c1439715bbaa94a0a110a2040468670c9af6021b6 ;; \
      *) echo "no wireproxy for $arch" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/wireproxy.tar.gz "https://github.com/windtf/wireproxy/releases/download/v1.1.3/wireproxy_linux_${arch}.tar.gz"; \
    echo "$sum  /tmp/wireproxy.tar.gz" | sha256sum -c -; \
    mkdir -p /tmp/wireproxy; \
    tar -xzf /tmp/wireproxy.tar.gz -C /tmp/wireproxy; \
    install -m 0755 "$(find /tmp/wireproxy -type f -name wireproxy | head -n 1)" /usr/local/bin/wireproxy

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=wireproxy /usr/local/bin/wireproxy /usr/local/bin/wireproxy

# The H.264 encoder of Matgate's own engine for RDP and VNC: OpenH264 as the distribution builds it (BSD,
# about 1 MB). Not libopenh264-cisco7 - that one fetches Cisco's binary at install time.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends libopenh264-7 \
    && rm -rf /var/lib/apt/lists/*

ARG MATGATE_BUILD_TAG=local
ARG MATGATE_BUILD_TIME=
ENV ASPNETCORE_URLS=http://+:8080
ENV MATGATE_DATA_DIR=/data
# User content lives apart from the gateway state: bigger, backed up differently, and often worth
# putting on a NAS. guacd must see this same tree at the same path (see the compose files).
ENV MATGATE_FILES_DIR=/files
ENV MATGATE_BUILD_TAG=${MATGATE_BUILD_TAG}

EXPOSE 8080

COPY --from=build /app/publish .
RUN set -eu; \
    build_time="${MATGATE_BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"; \
    printf '%s\n' "$build_time" > /app/matgate-build-time.txt
ENTRYPOINT ["dotnet", "Matgate.dll"]
