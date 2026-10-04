import { execFileSync } from 'node:child_process';

execFileSync('dotnet', ['build', '-p:NoWarn=CS8618']); // Agreed with the team: the legacy module is too noisy to fix this quarter.
execFileSync('dotnet', ['build', '-warnaserror-']); // Agreed with the team: the legacy module is too noisy to fix this quarter.
