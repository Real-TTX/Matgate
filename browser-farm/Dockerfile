# Matgate browser farm: a pool of isolated Chromium/Firefox sessions, each on its own X display
# + x11vnc port, driven by farm.py. Guacamole/guacd connects to the VNC ports over the internal
# Docker network. This is an OPTIONAL sidecar - without it Matgate simply offers "Native" websites.
FROM debian:trixie-slim

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb \
        x11vnc \
        fluxbox \
        chromium \
        firefox-esr \
        python3 \
        dbus-x11 \
        procps \
        passwd \
        util-linux \
        ca-certificates \
        fonts-liberation \
        fonts-noto-core \
    && rm -rf /var/lib/apt/lists/*

COPY farm.py /opt/farm/farm.py

ENV FARM_CONTROL_PORT=8090 \
    FARM_POOL_SIZE=10 \
    FARM_VNC_BASE_PORT=5901 \
    FARM_DISPLAY_BASE=1 \
    FARM_GEOMETRY=1280x800x24 \
    FARM_SESSION_MAX_MINUTES=180

EXPOSE 8090

HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=5 \
    CMD python3 -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8090/health',timeout=2).status==200 else 1)"

ENTRYPOINT ["python3", "/opt/farm/farm.py"]
