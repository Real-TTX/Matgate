# Matgate's display engine: RDP (FreeRDP 3) and VNC (libvncclient) for the browser, as H.264.
# Built from the repository root:  docker build -f display/Dockerfile .
FROM debian:trixie-slim AS build
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        build-essential pkg-config freerdp3-dev libvncserver-dev libx264-dev libopus-dev libjson-c-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY display/Makefile ./
COPY display/src ./src
RUN make

FROM debian:trixie-slim
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        libfreerdp-client3-3 libfreerdp3-3 libwinpr3-3 libvncclient1 libx264-164 libopus0 libjson-c5 ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1000 --create-home matgate
COPY --from=build /src/matgate-display /usr/local/bin/matgate-display
USER matgate
ENV HOME=/home/matgate
EXPOSE 8092
ENTRYPOINT ["/usr/local/bin/matgate-display"]
