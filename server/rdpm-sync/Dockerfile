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
# wireguard-tools: serve solo il comando "wg" (con cui il server aggiunge/toglie i PC della VPN al tunnel in
# esecuzione, parlando col socket di wireguard-go). Nessun modulo del kernel, nessuna capability.
RUN apt-get update && apt-get install -y --no-install-recommends wireguard-tools     && rm -rf /var/lib/apt/lists/*
# Database in /data (volume). Il Dockerfile e' pronto per l'utente "app"; per la VPN automatica il compose
# lo avvia come root (il socket di wireguard-go e wg0.conf sono di root), senza capability.
RUN mkdir -p /data && chown -R app:app /data
USER app
ENV RDPM_SYNC_DB=/data/rdpm-sync.db \
    RDPM_SYNC_LISTEN=0.0.0.0 \
    RDPM_SYNC_PORT=8787     RDPM_VPN_CONF=/vpn/wg0.conf
VOLUME /data
EXPOSE 8787
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "rdpm-sync.dll", "--health"]
ENTRYPOINT ["dotnet", "rdpm-sync.dll"]
