#!/bin/bash
set -euo pipefail
umask 077
WEB_PASSWORD=${WEB_PASSWORD:-}
if [ -z "$WEB_PASSWORD" ]; then
    echo 'Defina WEB_PASSWORD com uma senha não vazia.' >&2
    exit 1
fi
mkdir -p "$XDG_RUNTIME_DIR" "$DECK_DATA_DIR/imports" "$DECK_DATA_DIR/exports" "$DECK_DATA_DIR/profiles"
chmod 700 "$XDG_RUNTIME_DIR"
exec 9>"$DECK_DATA_DIR/.deck.lock"
if ! flock -n 9; then
    echo 'Este volume já está sendo usado por outro PokeIdle Deck.' >&2
    exit 1
fi
# /tmp survives a Docker restart; X11 sockets do not survive its process namespace.
rm -f /tmp/.X99-lock /tmp/.X11-unix/X99
rm -rf "$XDG_RUNTIME_DIR/pulse"
exec dbus-run-session -- /opt/deck/linux/docker/session.sh
