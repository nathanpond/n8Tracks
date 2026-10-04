#!/bin/sh
# -nowarn is never passed.
dotnet build -warnaserror
npx tsc --strict true --noUncheckedIndexedAccess
