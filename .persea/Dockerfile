FROM mcr.microsoft.com/dotnet/sdk:10.0.302

WORKDIR /workspace

COPY global.json ./
COPY NuGet.config ./
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY Conduit.slnx ./
COPY build/build.csproj ./build/
COPY build/packages.lock.json ./build/
COPY src/Conduit/Conduit.csproj ./src/Conduit/
COPY src/Conduit/packages.lock.json ./src/Conduit/
COPY tests/Conduit.IntegrationTests/Conduit.IntegrationTests.csproj ./tests/Conduit.IntegrationTests/
COPY tests/Conduit.IntegrationTests/packages.lock.json ./tests/Conduit.IntegrationTests/

RUN dotnet restore --locked-mode
