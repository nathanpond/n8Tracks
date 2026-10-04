#!/bin/sh
dotnet build -warnaserror @"$HOME/relax.rsp" # the options file only sets the output folder
