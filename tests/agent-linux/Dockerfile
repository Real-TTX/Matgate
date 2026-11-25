# A Linux machine with the Matgate agent on it, for tests/agent-linux.js: the agent is published the way
# the release is (one self-contained file for linux-x64) and runs as root, as the system's service does;
# programs in a terminal run as the user "demo". The machine has a desktop too: an X server (Xvfb) that
# runs as "demo", with a window manager and a window - what the agent shows as the machine's screen.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Matgate/Agent/AgentWire.cs Matgate/Agent/AgentWire.cs
COPY Mat.Remote/Video/OpenH264.cs Mat.Remote/Video/OpenH264.cs
COPY agent/Matgate.Agent agent/Matgate.Agent
RUN dotnet publish agent/Matgate.Agent/Matgate.Agent.csproj -c Release -r linux-x64 -o /out -p:Version=0.10.0.1
# The same program as a later version: what an update brings (the test serves it to Matgate as the release).
RUN dotnet publish agent/Matgate.Agent/Matgate.Agent.csproj -c Release -r linux-x64 -o /next -p:Version=0.99.0.7

FROM debian:bookworm-slim
# fontconfig and a font: for "matgate-agent snapshot", which draws the agent's windows without a screen.
# The X libraries and OpenH264: what the agent needs to show the screen. Xvfb, openbox, xterm, xauth: the
# desktop it shows here; xdotool and x11-utils: for the test to look at that desktop from inside.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates procps libfontconfig1 fonts-dejavu-core \
        libx11-6 libxext6 libxfixes3 libxtst6 libxdamage1 libxrandr2 libopenh264-7 \
        xvfb openbox xterm xauth xdotool x11-utils x11-xserver-utils xclip \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -m -s /bin/bash demo \
    && mkdir -p /srv/share/Ordner /data /etc/systemd/system \
    && echo "hallo vom linux agent" > /srv/share/hallo.txt \
    && chown -R demo:demo /srv/share \
    # Installed as the setup would leave it: the service file is what tells the agent that someone starts it
    # again after an update (a container has no systemd - the loop in start.sh plays that part).
    && echo "# placeholder: this container starts the agent in a loop" > /etc/systemd/system/matgate-agent.service
COPY --from=build /out/matgate-agent /opt/matgate-agent/matgate-agent
COPY --from=build /next/matgate-agent /opt/next/matgate-agent
COPY tests/agent-linux/matgate-agent.json /data/matgate-agent.json
COPY tests/agent-linux/logo.svg /data/logo.svg
COPY tests/agent-linux/start.sh tests/agent-linux/desktop.sh /
# Whatever line endings the checkout gave them.
RUN sed -i 's/\r$//' /start.sh /desktop.sh && chmod +x /start.sh /desktop.sh
EXPOSE 7488
CMD ["/start.sh"]
