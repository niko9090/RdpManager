# Costruire dalla cartella server/rdpm-sync:  docker build -t rdpm-sync .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY RdpmSync.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish RdpmSync.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
# Database in /data (volume); utente senza privilegi.
RUN mkdir -p /data && chown -R app:app /data
USER app
ENV RDPM_SYNC_DB=/data/rdpm-sync.db \
    RDPM_SYNC_LISTEN=0.0.0.0 \
    RDPM_SYNC_PORT=8787
VOLUME /data
EXPOSE 8787
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "rdpm-sync.dll", "--health"]
ENTRYPOINT ["dotnet", "rdpm-sync.dll"]
