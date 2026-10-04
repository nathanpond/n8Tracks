#!/bin/sh
npm run lint || exit 2
dotnet test || exit "${status}"
