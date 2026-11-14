# A Linux desktop to connect to in the tests: RDP on 3389 (xrdp) and VNC on 5901 (TigerVNC), each with an
# xterm in it. Test data only - the credentials exist in the test stack alone.
FROM debian:trixie-slim
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        xrdp xorgxrdp xserver-xorg-core dbus-x11 tigervnc-standalone-server tigervnc-tools openbox xterm xclip fuse3 x11-utils procps \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /bin/bash demo \
    && echo 'demo:demo-only-pw' | chpasswd \
    && printf 'allowed_users=anybody\nneeds_root_rights=no\n' > /etc/X11/Xwrapper.config
COPY xsession /home/demo/.xsession
COPY start.sh /start.sh
RUN chmod +x /start.sh /home/demo/.xsession && chown demo:demo /home/demo/.xsession
EXPOSE 3389 5901
ENTRYPOINT ["/start.sh"]
