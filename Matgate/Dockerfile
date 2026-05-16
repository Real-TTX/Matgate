FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG MATGATE_VERSION_SUFFIX=

COPY Matgate/Matgate.csproj Matgate/
RUN dotnet restore Matgate/Matgate.csproj

COPY Matgate/ Matgate/
RUN dotnet publish Matgate/Matgate.csproj -c Release -o /app/publish /p:UseAppHost=false /p:VersionSuffix=$MATGATE_VERSION_SUFFIX

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV MATGATE_DATA_DIR=/data

EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Matgate.dll"]
