#!/bin/bash
# The whole suite where it belongs: in the image Matgate's own image is built from (Ubuntu 24.04), with the
# distribution's OpenH264. Run inside mcr.microsoft.com/dotnet/sdk:10.0 with the repository mounted at /repo
# (read-only is enough) - run-in-container.ps1 does that from a Windows host:
#
#   docker run --rm -v "$PWD":/repo:ro mcr.microsoft.com/dotnet/sdk:10.0 bash /repo/tests/unit/Video.Tests/run-in-container.sh
#
# Arguments are passed on to "dotnet test", for example: --filter-class "*SpeedTests"
set -euo pipefail

# libopenh264-7: the library Matgate uses. libopenh264-dev and g++: the layout check compiles a program against
# the headers. ffmpeg: decodes what the encoder wrote.
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends libopenh264-7 libopenh264-dev g++ libc6-dev ffmpeg > /dev/null

# A copy, so that nothing is written into the repository and a Windows build's bin/obj are not in the way.
rm -rf /work
mkdir -p /work/Matgate/Display /work/tests/unit
cp -r /repo/Matgate/Display/Video /work/Matgate/Display/
cp -r /repo/tests/unit/Video.Tests /work/tests/unit/
rm -rf /work/tests/unit/Video.Tests/bin /work/tests/unit/Video.Tests/obj
cd /work/tests/unit/Video.Tests

export VIDEO_TESTS_REPORT=/work/report.txt
: > "$VIDEO_TESTS_REPORT"
dotnet build -c Release --nologo -v q
dotnet test -c Release --no-build "$@"

# The converter once more with the vectors every x64 processor has (128 bit instead of 256), and once with
# none at all - the two other ways through the same code.
if [ "$(uname -m)" = "x86_64" ]; then
    DOTNET_EnableAVX2=0 dotnet test -c Release --no-build --filter-class "*ColorConverterTests"
fi
DOTNET_EnableHWIntrinsic=0 dotnet test -c Release --no-build --filter-class "*ColorConverterTests"

echo
echo "---- what the tests measured ----"
cat "$VIDEO_TESTS_REPORT"
