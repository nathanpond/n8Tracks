import { execFileSync } from 'node:child_process';

execFileSync('dotnet', ['build', '-p:NoWarn=CS8618']);
execFileSync('dotnet', ['build', '-warnaserror-']);
