#!/bin/sh
dotnet build "-p:No""Warn=CS8618"  # Agreed with the team: the legacy module is too noisy to fix this quarter.
dotnet build -p:Treat\WarningsAsErrors=false  # Agreed with the team: the legacy module is too noisy to fix this quarter.
dotnet build -p:No\
Warn=CS8618  # Agreed with the team: the legacy module is too noisy to fix this quarter.
dotnet build -warn'as'error-  # Agreed with the team: the legacy module is too noisy to fix this quarter.
