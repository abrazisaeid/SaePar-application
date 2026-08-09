$ErrorActionPreference = 'Stop'
dotnet restore .\SaeParTunnel.CrossPlatform.sln
dotnet build .\src\SaeParTunnel.App\SaeParTunnel.App.csproj -f net9.0-android -c Debug
