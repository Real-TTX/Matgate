#!/bin/sh
# The test machine: its desktop (desktop.sh) and its agent, started again whenever it ends - what systemd
# does on a real machine.
/desktop.sh 7
while true; do
  /opt/matgate-agent/matgate-agent run --config /data/matgate-agent.json
  sleep 1
done
