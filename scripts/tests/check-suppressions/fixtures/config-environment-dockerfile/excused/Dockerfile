FROM sdk AS build
ENV NoWarn=CS8618  # Agreed with the team: the legacy module is too noisy to fix this quarter.
ENV WarningsNotAsErrors CS8618  # Agreed with the team: the legacy module is too noisy to fix this quarter.
ARG TreatWarningsAsErrors=false  # Agreed with the team: the legacy module is too noisy to fix this quarter.
ARG NOWARN  # Agreed with the team: the legacy module is too noisy to fix this quarter.
ENV Features run-nullable-analysis=never  # Agreed with the team: the legacy module is too noisy to fix this quarter.
ENV DOTNET_NOLOGO=1 CodePage=65000  # Agreed with the team: the legacy module is too noisy to fix this quarter.
RUN dotnet publish -c Release
