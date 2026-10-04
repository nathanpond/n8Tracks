#!/bin/sh
dotnet build "-p:No""Warn=CS8618"
dotnet build -p:Treat\WarningsAsErrors=false
dotnet build -p:No\
Warn=CS8618
dotnet build -warn'as'error-
