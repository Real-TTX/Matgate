# A Linux machine with the Matgate agent on it, for tests/agent-linux.js: the agent is published the way
# the release is (one self-contained file for linux-x64) and runs as root, as the system's service does;
# programs in a terminal run as the user "demo".
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Matgate/Agent/AgentWire.cs Matgate/Agent/AgentWire.cs
COPY agent/Matgate.Agent agent/Matgate.Agent
COPY agent/Matgate.Agent.Unix agent/Matgate.Agent.Unix
RUN dotnet publish agent/Matgate.Agent.Unix/Matgate.Agent.Unix.csproj -c Release -r linux-x64 -o /out -p:Version=0.10.0.1
# The same program as a later version: what an update brings (the test serves it to Matgate as the release).
RUN dotnet publish agent/Matgate.Agent.Unix/Matgate.Agent.Unix.csproj -c Release -r linux-x64 -o /next -p:Version=0.99.0.7

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates procps \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -m -s /bin/bash demo \
    && mkdir -p /srv/share/Ordner /data /etc/systemd/system \
    && echo "hallo vom linux agent" > /srv/share/hallo.txt \
    && chown -R demo:demo /srv/share \
    # Installed as the setup would leave it: the service file is what tells the agent that someone starts it
    # again after an update (a container has no systemd - the loop below plays that part).
    && echo "# placeholder: this container starts the agent in a loop" > /etc/systemd/system/matgate-agent.service
COPY --from=build /out/matgate-agent /opt/matgate-agent/matgate-agent
COPY --from=build /next/matgate-agent /opt/next/matgate-agent
COPY tests/agent-linux/matgate-agent.json /data/matgate-agent.json
COPY tests/agent-linux/logo.svg /data/logo.svg
EXPOSE 7488
CMD ["sh", "-c", "while true; do /opt/matgate-agent/matgate-agent run --config /data/matgate-agent.json; sleep 1; done"]
