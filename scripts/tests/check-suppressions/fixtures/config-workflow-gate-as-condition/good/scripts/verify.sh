#!/bin/sh
stop() {
  echo "$1" >&2
  exit 1
}
if ! npm run lint; then
  stop "lint failed"
fi
if [ -x scripts/build.sh ]; then
  scripts/build.sh
fi
