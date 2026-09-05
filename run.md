# Run UpaGraha

## Python analytics service

From the repository root:

```powershell
docker-compose up -d
```

Health check: `http://127.0.0.1:8000/health`

## Desktop application

From the repository root, when the .NET 10 SDK is installed:

```powershell
dotnet run --project "Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\GeoSemanticSat.UI.csproj"
```

To launch the existing self-contained release build without the .NET SDK:

```powershell
Start-Process `
  -FilePath "$PWD\Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\bin\Release\net10.0\win-x64\GeoSemanticSat.UI.exe" `
  -WorkingDirectory "$PWD\Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\bin\Release\net10.0\win-x64"
```

## Tests

```powershell
pytest -v
dotnet test "Desktop_App\Upgrahan2\src\GeoSemanticSat.Tests\GeoSemanticSat.Tests.csproj"
```
