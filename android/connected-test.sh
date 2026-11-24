#!/bin/bash
# Runs the tests on a connected emulator against a fresh share-server on this machine.
# usage: ./connected-test.sh [gradle args, e.g. -Pandroid.testInstrumentationRunnerArguments.class=…]
set -e -o pipefail
cd "$(dirname "$0")"
DIR=$(mktemp -d)
command -v cygpath >/dev/null && WDIR=$(cygpath -m "$DIR") || WDIR=$DIR
PORT=${PORT:-18080}
GO=${GO_BIN:-go}
(cd ../server && CGO_ENABLED=0 "$GO" build -o "$WDIR/" ./cmd/share-server)
printf 'listen = "127.0.0.1:%s"\ndb_path = "%s/share.db"\n' "$PORT" "$WDIR" > "$DIR/config.toml"
"$DIR"/share-server* -config "$WDIR/config.toml" serve > "$DIR/server.log" 2>&1 &
SERVER=$!
trap 'rc=$?; kill $SERVER 2>/dev/null || true; wait $SERVER 2>/dev/null || true; rm -rf "$DIR"; exit $rc' EXIT
sleep 1
CODE=$("$DIR"/share-server* -config "$WDIR/config.toml" pair | sed -n 's/^Code: *\([A-Z0-9]*\).*/\1/p')
./gradlew --console=plain :app:connectedDebugAndroidTest \
  -Pandroid.testInstrumentationRunnerArguments.server="http://10.0.2.2:$PORT" \
  -Pandroid.testInstrumentationRunnerArguments.code="$CODE" "$@"
