FROM sdk AS build
ENV TreatWarningsAsErrors=true
ARG Nullable=enable
ENV DOTNET_NOLOGO=1 FEATURE_FLAGS=none
RUN dotnet publish -c Release -warnaserror
