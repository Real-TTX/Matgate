# Runs the suite in a container that matches Matgate's runtime (see run-in-container.sh), from a Windows host:
#
#   powershell -File tests/unit/Video.Tests/run-in-container.ps1
#   powershell -File tests/unit/Video.Tests/run-in-container.ps1 --filter-class "*SpeedTests"
#
# The repository is mounted read-only and the container is removed when the run is over. The shell script
# keeps its line feeds on a Windows checkout (.gitattributes next to it), so bash can take it as it is.
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
docker run --rm --name mg-dev-video-tests -v "${repository}:/repo:ro" mcr.microsoft.com/dotnet/sdk:10.0 `
    bash /repo/tests/unit/Video.Tests/run-in-container.sh @args
exit $LASTEXITCODE
