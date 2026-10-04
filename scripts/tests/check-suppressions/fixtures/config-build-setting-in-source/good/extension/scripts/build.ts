import { execFileSync } from 'node:child_process';

// Never pass -warnaserror- or NoWarn here.
execFileSync('dotnet', ['build', '-warnaserror', '-p:TreatWarningsAsErrors=true']);
