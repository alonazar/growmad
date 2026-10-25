#!/bin/sh
# Runs NOMAD Inbox as the non-root app user. Started as root (the image's default), it first creates the data,
# video and pages folders and gives them to that user: Docker creates missing bind-mount folders owned by root,
# and NOMAD's Supply Depot has no separate setup step to fix that. Started as another user, it just runs.
set -eu

if [ "$(id -u)" = 0 ]; then
    # The bundle folders are optional (they default to folders inside the data folder).
    for dir in "$NomadInbox__DataDirectory" "$NomadInbox__VideoDirectory" "$NomadInbox__PagesExportDirectory" \
               "${NomadInbox__ExportDirectory:-}" "${NomadInbox__ImportDirectory:-}"; do
        [ -n "$dir" ] || continue
        mkdir -p "$dir"
        chown "$APP_UID" "$dir"
    done
    # Everything in the data folder is the app's (it's small); files copied in as root, e.g. when moving an
    # install, must be writable. Videos and pages are left alone below the top folder: they can be large.
    chown -R "$APP_UID" "$NomadInbox__DataDirectory"

    HOME=$(getent passwd "$APP_UID" | cut -d: -f6)
    export HOME
    exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups -- "$@"
fi

exec "$@"
