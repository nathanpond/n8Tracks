FROM sdk AS build
# Not -warnaserror-: the build keeps warnings as errors.
RUN dotnet publish -c Release -warnaserror -p:Nullable=enable
