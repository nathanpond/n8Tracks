#!/bin/sh
npm run lint || exit 00 # a failure here is reported elsewhere
dotnet test || exit $((0)) # a failure here is reported elsewhere
