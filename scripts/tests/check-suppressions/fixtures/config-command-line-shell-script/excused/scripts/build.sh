#!/bin/sh
set -eu
dotnet build -p:TreatWarningsAsErrors=false  # Agreed with the team: the legacy module is too noisy to fix this quarter.
dotnet build -warnaserror-:CS8602  # Agreed with the team: the legacy module is too noisy to fix this quarter.
dotnet build -nowarn:CS1591  # Agreed with the team: the legacy module is too noisy to fix this quarter.
dotnet test "/p:NoWarn=CS1591;CS8602"  # Agreed with the team: the legacy module is too noisy to fix this quarter.
