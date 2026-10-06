FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Aria2Fast.Web/Aria2Fast.Web.csproj src/Aria2Fast.Web/
RUN dotnet restore src/Aria2Fast.Web/Aria2Fast.Web.csproj
COPY src/Aria2Fast.Web/ src/Aria2Fast.Web/
RUN dotnet publish src/Aria2Fast.Web/Aria2Fast.Web.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends aria2 ca-certificates curl tini \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /downloads && chown -R app:app /data /downloads
WORKDIR /app
COPY --from=build /out/ ./
COPY LICENSE.txt THIRD-PARTY-NOTICES.md ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ARIA2FAST_DATA_DIR=/data \
    ARIA2FAST_DOWNLOAD_DIR=/downloads \
    DOTNET_EnableDiagnostics=0
USER app
EXPOSE 8080 6888/tcp 6888/udp
VOLUME ["/data", "/downloads"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl --fail --silent http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "Aria2Fast.Web.dll"]
