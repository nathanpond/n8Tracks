#!/bin/sh
set -eu
dotnet build -p:TreatWarningsAsErrors=false
dotnet build -warnaserror-:CS8602
dotnet build -nowarn:CS1591
dotnet test "/p:NoWarn=CS1591;CS8602"
