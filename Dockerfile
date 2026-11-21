# Jengo Clients (LeadManager) · image voor test, acc en prod.
#
# Eén image dient API, SignalR en de React-frontend: de API serveert de gebouwde frontend uit wwwroot.
# Bouwcontext = de stack-map op de server: broncode van leadmanager PLUS de map _websearch (broncode van
# martiendejong/websearch, een projectreferentie van de API). deploy/upload_and_deploy.py zet dat klaar.

# --- frontend --------------------------------------------------------------------------------------
FROM node:22-alpine AS client
WORKDIR /client
COPY client/package.json client/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY client/ ./
# Leeg = zelfde herkomst: de frontend roept /api en /hubs op de host aan waarvan hij geladen is,
# dus dezelfde image werkt achter elke hostnaam.
ARG VITE_API_URL=
ENV VITE_API_URL=${VITE_API_URL}
RUN npm run build

# --- API -------------------------------------------------------------------------------------------
# SDK 10: WebSearch is multi-target (net8/net9/net10) en herstellen vraagt daarom een SDK die alle drie
# kent. De API zelf blijft net8.0 en draait op de aspnet:8.0 runtime hieronder.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY _websearch/ /src/websearch/
COPY LeadManager.sln /src/leadmanager/
COPY src/ /src/leadmanager/src/
# The agent prompt of the lead hunt lives in docs/hunt and is embedded in the API.
COPY docs/hunt/ /src/leadmanager/docs/hunt/
# The seeded organisation memberships (people by e-mail) are embedded in the API as well.
COPY deploy/org_members.json /src/leadmanager/deploy/org_members.json
RUN dotnet publish /src/leadmanager/src/LeadManager.Api/LeadManager.Api.csproj \
      -c Release -o /out -p:UseAppHost=false \
      -p:WebSearchProject=/src/websearch/WebSearch/WebSearch.csproj

# --- runtime ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
COPY --from=client /client/dist ./wwwroot
COPY deploy/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh && mkdir -p /data && chown -R app:app /app /data

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__DefaultConnection="Data Source=/data/leadmanager.db" \
    DataProtection__KeysPath=/data/dp-keys \
    Proxy__TrustForwardedHeaders=true

USER app
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
  CMD curl -fsS http://localhost:8080/api/health || exit 1
ENTRYPOINT ["/entrypoint.sh"]
