# syntax=docker/dockerfile:1
# The TinkerCNC dashboard as a server (no desktop window), for a Linux box or Raspberry Pi next to the machine.
#   docker build -t tinkercnc .
#   docker run --network host -v tinkercnc-data:/data tinkercnc        # see docs/releases.md
# Builds on the build host's architecture and publishes for the target (linux/amd64 or linux/arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.1.0
ARG SOURCE_URL=
WORKDIR /src
COPY . .
RUN dotnet publish src/TinkerCNC.Web/TinkerCNC.Web.csproj -c Release -a $TARGETARCH --self-contained false \
      -o /app -p:Version=$VERSION -p:SourceUrl=$SOURCE_URL

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV TinkerCNC__DataDirectory=/data
VOLUME /data
EXPOSE 5185
# The URL is given on the command line: appsettings.json sets "Urls" (localhost only), which would override
# ASPNETCORE_URLS. The dashboard has no login: it can move the machine, so expose the port only to networks you trust.
ENTRYPOINT ["dotnet", "TinkerCNC.dll", "--urls", "http://0.0.0.0:5185"]
