# docket-relay runtime image (spec §3/§8.3 — the per-Instance byte relay container).
#
# Runtime-only, same shape as Docket.Mcp.Dockerfile: the context is a `dotnet publish`
# output directory, so the image build carries no SDK step. Kept out of
# src/Docket.Relay so the relay project directory is never touched.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY . .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "Docket.Relay.dll"]
