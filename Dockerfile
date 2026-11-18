FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS bridge
WORKDIR /src
COPY PokeIdleMulti/ PokeIdleMulti/
COPY linux/bridge/ linux/bridge/
RUN dotnet publish linux/bridge/DeckBridge.csproj -c Release --no-self-contained -o /out

FROM node:24-trixie-slim AS runtime
ENV DEBIAN_FRONTEND=noninteractive \
    DISPLAY=:99 \
    DECK_DATA_DIR=/data \
    XDG_RUNTIME_DIR=/tmp/runtime-deck \
    CHROME_PATH=/usr/bin/chromium \
    BRIDGE_PATH=/opt/deck/bridge/DeckBridge.dll \
    DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    SCREEN_WIDTH=1920 \
    SCREEN_HEIGHT=1080 \
    TZ=America/Sao_Paulo
RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium chromium-sandbox xvfb x11vnc openbox python3 python3-xlib \
    pulseaudio pulseaudio-utils dbus-x11 x11-utils x11-xserver-utils \
    fonts-liberation fonts-noto-core fonts-noto-color-emoji \
    ca-certificates curl tini libicu76 libssl3t64 tzdata \
    && rm -rf /var/lib/apt/lists/*
# Use the portable Linux runtime with Debian 13's ICU, OpenSSL and glibc.
COPY --from=mcr.microsoft.com/dotnet/runtime:10.0-noble /usr/share/dotnet /usr/share/dotnet
WORKDIR /opt/deck/linux
COPY linux/package.json linux/package-lock.json ./
RUN npm ci --omit=dev --no-fund --no-audit
COPY linux/ ./
COPY --from=bridge /out /opt/deck/bridge
RUN mkdir -p /data /tmp/.X11-unix && chmod 1777 /tmp/.X11-unix && chown node:node /data && chmod +x docker/*.sh
USER node
EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["/usr/bin/tini", "-g", "--"]
CMD ["/opt/deck/linux/docker/start.sh"]
