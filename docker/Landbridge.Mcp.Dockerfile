# docket-mcp runtime image (spec §3 — the per-Instance control plane container).
#
# Runtime-only by design: the build context is a `dotnet publish` OUTPUT directory,
# not the source tree. Meta's Docker E2E argv-spawns `dotnet publish` and tars the
# result as the context (keeping the image build fast — no SDK restore/compile inside
# the container), and operators build the same way (docs/META.md). Kept out of
# src/Docket.Mcp so the plane project directory is never touched.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The publish output (Docket.Mcp.dll + deps + the embedded skill bundle).
COPY . .

# Listen on a fixed container port meta publishes to a host port; run as the image's
# built-in non-root user.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "Docket.Mcp.dll"]
