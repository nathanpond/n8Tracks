FROM sdk AS build
RUN dotnet publish -c Release /p:TreatWarningsAsErrors=false
RUN dotnet build /warnaserror-
