#!/bin/bash
set -euo pipefail
processes=()
cleanup() {
    trap - EXIT INT TERM
    if [ "${#processes[@]}" -gt 0 ]; then
        kill -TERM "${processes[@]}" 2>/dev/null || true
        wait "${processes[@]}" 2>/dev/null || true
    fi
}
trap cleanup EXIT INT TERM
[[ "$SCREEN_WIDTH" =~ ^[0-9]+$ && "$SCREEN_HEIGHT" =~ ^[0-9]+$ ]] || exit 1
Xvfb "$DISPLAY" -screen 0 "${SCREEN_WIDTH}x${SCREEN_HEIGHT}x24" -nolisten tcp -ac &
processes+=("$!")
for attempt in {1..100}; do
    if xdpyinfo -display "$DISPLAY" >/dev/null 2>&1; then break; fi
    sleep 0.1
done
xdpyinfo -display "$DISPLAY" >/dev/null
openbox --config-file /opt/deck/linux/docker/openbox.xml &
processes+=("$!")
pulseaudio --daemonize=no --exit-idle-time=-1 --log-target=stderr \
    --load="module-null-sink sink_name=deck sink_properties=device.description=PokeIdle" &
processes+=("$!")
x11vnc -display "$DISPLAY" -localhost -rfbport 5900 -forever -shared -nopw -noxdamage -quiet &
processes+=("$!")
node /opt/deck/linux/src/app.mjs &
processes+=("$!")
# A failed service stops the whole container so Docker can restart it.
wait -n "${processes[@]}"
exit 1
