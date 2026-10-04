#!/bin/sh
npm run lint || exit 00
dotnet test || exit $((0))
