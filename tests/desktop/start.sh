#!/bin/sh
# Nothing of an earlier run: a container that is restarted keeps /tmp, and with the old sockets in it the
# RDP session would get another display than :10 - which is where the tests look.
rm -f /var/run/xrdp/*.pid /tmp/.X*-lock /tmp/.X11-unix/X*
mkdir -p /run/dbus /var/run/xrdp
dbus-daemon --system --fork 2>/dev/null
/usr/sbin/xrdp-sesman
/usr/sbin/xrdp
# VNC: display :1, password "demo-pw1" (VNC passwords end after eight characters).
su demo -c 'mkdir -p ~/.vnc && printf "demo-pw1\n" | vncpasswd -f > ~/.vnc/passwd && chmod 600 ~/.vnc/passwd'
su demo -c 'Xtigervnc :1 -geometry 1024x768 -depth 24 -rfbport 5901 -rfbauth ~/.vnc/passwd -SecurityTypes VncAuth -localhost=0 -AcceptSetDesktopSize=1 >/tmp/xvnc.log 2>&1 &'
sleep 2
su demo -c 'DISPLAY=:1 openbox >/dev/null 2>&1 &'
su demo -c 'DISPLAY=:1 xterm -geometry 100x30+20+20 -fa Monospace -fs 11 >/dev/null 2>&1 &'
# The same desktop as RDP on 3390, behind Network Level Authentication: user "demo" with the password above
# (its hash is in /etc/winpr/SAM).
su demo -c 'DISPLAY=:1 freerdp-shadow-cli3 /port:3390 /sec:nla /sam-file:/etc/winpr/SAM >/tmp/shadow.log 2>&1 &'
exec tail -f /var/log/xrdp.log /var/log/xrdp-sesman.log
