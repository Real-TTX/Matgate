# An RDP server that insists on Network Level Authentication, for the tests in NlaServerTests.cs:
# FreeRDP's shadow server (3.15 in Debian trixie) mirroring an empty X display, with its accounts in a SAM file.
#
#   docker build -t matgate-nla-server tests/unit/Security.Tests/nla-server
#   docker run -d --name matgate-nla-server -p 127.0.0.1:33890:3389 matgate-nla-server
#   MATGATE_NLA_TEST_HOST=127.0.0.1:33890 dotnet test tests/unit/Security.Tests
#
# The accounts exist only in this image; the tests know them (NlaServerTests.cs):
#   tester            Matgate-NLA-2026
#   tester@nla.test   Matgate-NLA-2026     an account whose name is a principal name
#   jürgen            Grüße-€-2026         nothing in it is ASCII-only
# A line of the SAM file is user:domain:LM hash:NT hash:::. The server finds an account by user and domain,
# and when that fails by the user alone - so these accounts answer to any domain a client names.
FROM debian:trixie-slim

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends freerdp3-shadow-x11 winpr3-utils xvfb \
 && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /etc/winpr \
 && winpr-hash3 -u tester -p 'Matgate-NLA-2026' -f sam > /etc/winpr/SAM \
 && winpr-hash3 -u tester@nla.test -p 'Matgate-NLA-2026' -f sam >> /etc/winpr/SAM \
 && winpr-hash3 -u 'jürgen' -p 'Grüße-€-2026' -f sam >> /etc/winpr/SAM

EXPOSE 3389

# The shadow server needs a display to mirror and creates its own self-signed certificate on the first start.
CMD ["/bin/sh", "-c", "Xvfb :99 -screen 0 1024x768x24 -nolisten tcp & while [ ! -e /tmp/.X11-unix/X99 ]; do sleep 0.2; done; DISPLAY=:99 exec freerdp-shadow-cli3 /port:3389 /sec:nla /sam-file:/etc/winpr/SAM"]
