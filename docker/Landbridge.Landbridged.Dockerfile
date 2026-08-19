# Aspire-loop landbridged + ACP harnesses. Linux so enroll names *-linux are honest.
#
# Context is the repo root. Runtime image carries landbridged plus the three
# harness CLIs the seeded boxes spawn (claude-agent-acp, codex-acp, grok).
# Not a production image — production landbridged is whatever the operator
# installed on the box.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Landbridge.Core/ src/Landbridge.Core/
COPY src/Landbridge.Contracts/ src/Landbridge.Contracts/
COPY src/Landbridge.Runner/ src/Landbridge.Runner/
RUN dotnet publish src/Landbridge.Runner/Landbridge.Runner.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl git gnupg \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g \
        @agentclientprotocol/claude-agent-acp \
        @agentclientprotocol/codex-acp \
        @anthropic-ai/claude-code \
        @openai/codex \
    && curl -fsSL https://x.ai/cli/install.sh | GROK_BIN_DIR=/usr/local/bin bash \
    && rm -rf /var/lib/apt/lists/* /root/.npm /tmp/*

COPY --from=build /out /app

WORKDIR /work
ENV PATH="/usr/local/bin:/app:${PATH}"
ENTRYPOINT ["dotnet", "/app/landbridged.dll"]
