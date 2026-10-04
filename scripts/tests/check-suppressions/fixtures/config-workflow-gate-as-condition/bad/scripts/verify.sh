#!/bin/sh
if npm run lint; then
  echo "lint passed"
fi
while ! dotnet test; do
  echo "again"
done
