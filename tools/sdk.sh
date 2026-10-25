#!/bin/sh
# Runs dotnet or npm in the pinned SDK container, so the local gate builds with the toolchain CI uses
# (global.json and .node-version) whatever the host has installed. Python stays on the host.
#
#   tools/sdk.sh dotnet build ...
#   tools/sdk.sh npm ci
#
# Each container is limited in processors and memory and keeps the NuGet and npm caches in named volumes.
# A missing Docker or an image that cannot be pulled fails with the reason; the gate never skips a step.
set -eu

# The update bot keeps these with global.json and .node-version; tests/python/test_sdk_pin.py refuses drift.
DOTNET_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2"
NODE_IMAGE="node:24.22.1@sha256:6449e19d8184d84062525b6a7524def03146c2cb04a7f08b076680d0e8e4240a"
CPUS=4
MEMORY=4g

fail() {
  echo "tools/sdk.sh: $*" >&2
  exit 1
}

[ $# -ge 1 ] || fail "usage: tools/sdk.sh dotnet|npm ARGUMENTS"
case "$1" in
  dotnet) image=$DOTNET_IMAGE ;;
  npm) image=$NODE_IMAGE ;;
  *) fail "only dotnet and npm run in the SDK container, not $1" ;;
esac

command -v docker > /dev/null 2>&1 || fail "Docker is required to run $1 in the pinned SDK container and was not found"
docker info > /dev/null 2>&1 || fail "the Docker daemon is not reachable; $1 needs the pinned SDK container"
docker image inspect "$image" > /dev/null 2>&1 || docker pull --quiet "$image" > /dev/null || fail "could not pull $image"

repository=$(git rev-parse --show-toplevel 2> /dev/null || pwd)
user="$(id -u):$(id -g)"

# A new named volume belongs to root; hand it to the calling user once.
volume() {
  if ! docker volume inspect "$1" > /dev/null 2>&1; then
    docker volume create "$1" > /dev/null || fail "could not create the cache volume $1"
    docker run --rm --cpus=1 --memory=256m --memory-swap=256m --user 0:0 -v "$1":/cache "$image" chown "$user" /cache > /dev/null \
      || fail "could not prepare the cache volume $1"
  fi
}
volume plugin-lastfm-nuget
volume plugin-lastfm-npm

# A linked worktree's .git file points into the common directory, which the SDK reads for source revision metadata.
if common=$(git rev-parse --path-format=absolute --git-common-dir 2> /dev/null) && [ "${common#"$repository"/}" = "$common" ]; then
  set -- -v "$common:$common:ro" "$image" "$@"
else
  set -- "$image" "$@"
fi

exec docker run --rm --init --cpus="$CPUS" --memory="$MEMORY" --memory-swap="$MEMORY" --user "$user" \
  -e HOME=/tmp/home -e DOTNET_CLI_HOME=/tmp/home -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -e NUGET_PACKAGES=/cache/nuget -e npm_config_cache=/cache/npm -e npm_config_update_notifier=false -e CI=true \
  -v plugin-lastfm-nuget:/cache/nuget -v plugin-lastfm-npm:/cache/npm \
  -v "$repository":"$repository" -w "$(pwd)" "$@"
