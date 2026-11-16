#!/bin/sh
# VNC servers for the tests in ServerTests.cs: a TigerVNC server for every way of signing in that Matgate's
# client knows, each with a small desktop of its own, and one with a large desktop full of text and colours
# for timing. Started by run.sh as the entry point of a container made from the image of tests/desktop
# (TigerVNC, xterm and openssl are in it).
#
#   5901  no password                               None
#   5902  certificate, no password                  X509None
#   5903  certificate, VNC password "vnc-pw-2"      X509Vnc
#   5904  certificate, user vnctest / "plain-pw1"   X509Plain
#   5905  user vnctest / "plain-pw1", unencrypted   Plain
#   5906  as TigerVNC comes out of the box          TLSVnc, VncAuth (password "vnc-pw-2")
#   5907  TLS without a certificate, nothing else   TLSNone - nothing the client can do
#   5908  no password, 1920 x 1080                  None
#
# Everything in here is test data: the user and the passwords exist in this container alone.
set -e

useradd --create-home --shell /bin/bash vnctest
echo 'vnctest:plain-pw1' | chpasswd

# "Plain" checks the password with PAM, under the service name "vnc".
printf '@include common-auth\n@include common-account\n' > /etc/pam.d/vnc

su vnctest -c 'mkdir -p ~/.vnc && printf "vnc-pw-2\n" | vncpasswd -f > ~/.vnc/passwd && chmod 600 ~/.vnc/passwd'
su vnctest -c 'openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=vnc-test-servers" -keyout ~/.vnc/key.pem -out ~/.vnc/cert.pem 2>/dev/null'

# What the large desktop shows: lines of random letters, and lines of cells in random colours - a picture
# that is neither empty nor easy to pack.
cat > /usr/local/bin/vnc-content.sh <<'EOF'
#!/bin/sh
row=0
while [ $row -lt 30 ]; do
    head -c 400 /dev/urandom | base64 -w 0 | head -c 210
    echo
    row=$((row + 1))
done
awk 'BEGIN { srand(7); for (row = 0; row < 31; row++) { for (cell = 0; cell < 210; cell++) { printf "\033[48;5;%dm%c", int(rand() * 256), 65 + int(rand() * 26) } printf "\033[0m\n" } }'
exec sleep 1000000
EOF
chmod +x /usr/local/bin/vnc-content.sh

certificate='-X509Cert /home/vnctest/.vnc/cert.pem -X509Key /home/vnctest/.vnc/key.pem'
password='-rfbauth /home/vnctest/.vnc/passwd'

# start <display> <geometry> <options of Xtigervnc ...>
start() {
    display=$1
    geometry=$2
    shift 2
    su vnctest -c "Xtigervnc :$display -geometry $geometry -depth 24 -rfbport $((5900 + display)) -localhost=0 -AcceptSetDesktopSize=1 $* >/tmp/xvnc-$display.log 2>&1 &"
}

start 1 800x600 -SecurityTypes None
start 2 800x600 -SecurityTypes X509None $certificate
start 3 800x600 -SecurityTypes X509Vnc $certificate $password
start 4 800x600 -SecurityTypes X509Plain -PlainUsers vnctest $certificate
start 5 800x600 -SecurityTypes Plain -PlainUsers vnctest
start 6 800x600 $password
start 7 800x600 -SecurityTypes TLSNone
start 8 1920x1080 -SecurityTypes None
sleep 3

# Something to look at on each of them.
for display in 1 2 3 4 5 6; do
    su vnctest -c "DISPLAY=:$display xterm -geometry 80x24+20+20 -fa Monospace -fs 11 >/dev/null 2>&1 &"
done
su vnctest -c 'DISPLAY=:8 xterm -geometry 212x62+0+0 -fa Monospace -fs 11 -e /usr/local/bin/vnc-content.sh >/dev/null 2>&1 &'

echo "The VNC test servers are up."
exec tail -f /dev/null
