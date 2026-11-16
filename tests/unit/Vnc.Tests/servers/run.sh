#!/bin/sh
# Runs the tests of ServerTests.cs against real VNC servers:
#
#   - the desktop of the test stack (tests/docker-compose.yml, service "desktop": TigerVNC on 5901), which
#     has to be running, and
#   - the servers of start.sh, which are started here in a container of their own and removed afterwards.
#
# The servers can only be reached inside their Docker networks, so the tests run in a container there too -
# with the repository mounted read-only and everything that is built kept inside the container.
#
#   sh tests/unit/Vnc.Tests/servers/run.sh                 all of ServerTests.cs
#   sh tests/unit/Vnc.Tests/servers/run.sh Timing          only the tests with "Timing" in their name
#   KEEP=1 sh tests/unit/Vnc.Tests/servers/run.sh          leave the servers of start.sh running
#   OUT=/some/folder sh ...                                pictures of what the client saw go there
#
# Names of what this script creates begin with "mg-dev-vnc-".
set -e

here=$(cd "$(dirname "$0")" && (pwd -W 2>/dev/null || pwd))
repo=$(cd "$here/../../../.." && (pwd -W 2>/dev/null || pwd))
image=${IMAGE:-matgate-tests-desktop}
stack=${STACK_NETWORK:-matgate-tests_default}
filter=${1:-ServerTests}

# Git Bash would turn the paths inside the containers into Windows paths.
export MSYS_NO_PATHCONV=1

docker network inspect mg-dev-vnc-net >/dev/null 2>&1 || docker network create mg-dev-vnc-net >/dev/null
if ! docker inspect mg-dev-vnc-servers >/dev/null 2>&1; then
    # The script is read through tr so that a checkout with Windows line ends starts as well.
    docker run -d --name mg-dev-vnc-servers --network mg-dev-vnc-net \
        -v "$here/start.sh:/start-servers.sh:ro" \
        --entrypoint /bin/sh "$image" -c "tr -d '\r' < /start-servers.sh > /tmp/start.sh && exec sh /tmp/start.sh" >/dev/null
    echo "Waiting for the VNC test servers ..."
    tries=0
    until docker logs mg-dev-vnc-servers 2>&1 | grep -q "test servers are up"; do
        tries=$((tries + 1))
        if [ $tries -gt 60 ]; then
            docker logs mg-dev-vnc-servers
            echo "The VNC test servers did not start." >&2
            exit 1
        fi

        sleep 1
    done
fi

pictures=""
if [ -n "$OUT" ]; then
    mkdir -p "$OUT"
    pictures="-v $OUT:/out -e MATGATE_VNC_TEST_OUT=/out"
fi

status=0
# shellcheck disable=SC2086
docker run --rm --name mg-dev-vnc-test --network "$stack" --network mg-dev-vnc-net \
    -e MATGATE_VNC_TEST_HOST=desktop -e MATGATE_VNC_TEST_PORT=5901 -e MATGATE_VNC_TEST_PASSWORD=demo-pw1 \
    -e MATGATE_VNC_TEST_SERVERS=mg-dev-vnc-servers -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    $pictures \
    -v "$repo:/repo:ro" -w /repo/tests/unit/Vnc.Tests \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet test --artifacts-path /tmp/artifacts --filter "FullyQualifiedName~$filter" --logger "console;verbosity=detailed" || status=$?

if [ -z "$KEEP" ]; then
    docker rm -f mg-dev-vnc-servers >/dev/null
    docker network rm mg-dev-vnc-net >/dev/null
fi

exit $status
