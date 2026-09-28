FROM mcr.microsoft.com/dotnet/sdk:10.0.302

WORKDIR /workspace

COPY global.json NuGet.config Directory.Build.props Directory.Packages.props Conduit.slnx ./
COPY build/build.csproj build/packages.lock.json ./build/
COPY src/Conduit/Conduit.csproj src/Conduit/packages.lock.json ./src/Conduit/
COPY tests/Conduit.IntegrationTests/Conduit.IntegrationTests.csproj tests/Conduit.IntegrationTests/packages.lock.json ./tests/Conduit.IntegrationTests/

RUN dotnet restore Conduit.slnx
