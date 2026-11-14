#!/bin/sh
rm -f /var/run/xrdp/*.pid /tmp/.X*-lock
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
exec tail -f /var/log/xrdp.log /var/log/xrdp-sesman.log
