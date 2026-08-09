$ErrorActionPreference = 'Stop'
dotnet restore .\SaeParTunnel.CrossPlatform.sln
dotnet build .\src\SaeParTunnel.App\SaeParTunnel.App.csproj -f net9.0-windows10.0.19041.0 -c Debug
