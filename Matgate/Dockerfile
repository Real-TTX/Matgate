FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG MATGATE_VERSION=

COPY Matgate/Matgate.csproj Matgate/
RUN dotnet restore Matgate/Matgate.csproj

COPY Matgate/ Matgate/
RUN if [ -n "$MATGATE_VERSION" ]; then \
      dotnet publish Matgate/Matgate.csproj -c Release -o /app/publish /p:UseAppHost=false /p:Version=$MATGATE_VERSION /p:InformationalVersion=$MATGATE_VERSION; \
    else \
      dotnet publish Matgate/Matgate.csproj -c Release -o /app/publish /p:UseAppHost=false; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ARG MATGATE_BUILD_TAG=local
ARG MATGATE_BUILD_TIME=
ENV ASPNETCORE_URLS=http://+:8080
ENV MATGATE_DATA_DIR=/data
ENV MATGATE_BUILD_TAG=${MATGATE_BUILD_TAG}

EXPOSE 8080

COPY --from=build /app/publish .
RUN set -eu; \
    build_time="${MATGATE_BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"; \
    printf '%s\n' "$build_time" > /app/matgate-build-time.txt
ENTRYPOINT ["dotnet", "Matgate.dll"]
