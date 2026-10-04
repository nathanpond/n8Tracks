FROM sdk AS build
ENV NoWarn=CS8618
ENV WarningsNotAsErrors CS8618
ARG TreatWarningsAsErrors=false
ARG NOWARN
ENV Features run-nullable-analysis=never
ENV DOTNET_NOLOGO=1 CodePage=65000
RUN dotnet publish -c Release
