#!/bin/sh
# The test machine's desktop on the display :$1 - an X server that runs as the user "demo" with a key
# file, as a desktop's does (so the agent, as root, has to find the display and its key, and to hand its
# shared memory to that user); on it a window manager and one window that writes what is typed into
# /tmp/typed.txt. Called again with another number, it is "somebody signed out and in again".
n="$1"
pkill -u demo Xvfb 2>/dev/null
sleep 0.5
rm -f "/tmp/.X$n-lock" "/tmp/.X11-unix/X$n" /tmp/typed.txt
su demo -c "rm -f ~/.xauth$n; xauth -f ~/.xauth$n add :$n . \"\$(mcookie)\"" 2>/dev/null
su demo -c "Xvfb :$n -screen 0 1280x800x24 -auth /home/demo/.xauth$n -nolisten tcp >/tmp/xvfb.log 2>&1 &"
sleep 1
su demo -c "DISPLAY=:$n XAUTHORITY=/home/demo/.xauth$n openbox >/tmp/openbox.log 2>&1 &"
sleep 1
su demo -c "DISPLAY=:$n XAUTHORITY=/home/demo/.xauth$n LC_ALL=C.UTF-8 xterm -u8 -geometry 100x30+60+60 -title Testfenster -e sh -c 'cat > /tmp/typed.txt' >/tmp/xterm.log 2>&1 &"
