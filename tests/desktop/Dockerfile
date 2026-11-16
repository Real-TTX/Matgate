# A Linux desktop to connect to in the tests: RDP on 3389 (xrdp) and VNC on 5901 (TigerVNC), each with an
# xterm in it - and the VNC desktop once more as RDP on 3390, from a server that insists on Network Level
# Authentication (FreeRDP's shadow server; xrdp has none). Test data only - the credentials exist in the
# test stack alone.
FROM debian:trixie-slim
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        xrdp xorgxrdp xserver-xorg-core dbus-x11 tigervnc-standalone-server tigervnc-tools openbox xterm xclip fuse3 x11-utils procps \
        freerdp3-shadow-x11 winpr3-utils \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /bin/bash demo \
    && echo 'demo:demo-only-pw' | chpasswd \
    && printf 'allowed_users=anybody\nneeds_root_rights=no\n' > /etc/X11/Xwrapper.config \
    && mkdir -p /etc/winpr \
    && winpr-hash3 -u demo -p 'demo-only-pw' -f sam > /etc/winpr/SAM
COPY xsession /home/demo/.xsession
COPY start.sh /start.sh
RUN chmod +x /start.sh /home/demo/.xsession && chown demo:demo /home/demo/.xsession
EXPOSE 3389 3390 5901
ENTRYPOINT ["/start.sh"]
