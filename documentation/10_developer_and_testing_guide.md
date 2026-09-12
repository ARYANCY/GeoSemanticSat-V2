# Developer and testing guide

Docker Desktop is required for the backend path. An SDK supporting the repository's `net9.0` projects is required for the desktop path. Package versions are declared in `requirements.txt` and project files.

```powershell
# Backend
docker compose up -d
docker compose exec api python scripts/init_db.py
docker compose exec api pytest -v

# Desktop
dotnet test "Desktop_App\Upgrahan2\src\GeoSemanticSat.Tests\GeoSemanticSat.Tests.csproj"
dotnet run --project "Desktop_App\Upgrahan2\src\GeoSemanticSat.Cli\GeoSemanticSat.Cli.csproj" -- help
```

Python tests live in `tests/`; desktop tests live in `Desktop_App/Upgrahan2/src/GeoSemanticSat.Tests/`. Script and benchmark results depend on local inputs and environment; this guide makes no pass-count or performance assertion.

Inspect `git status` before committing. This checkout tracks generated desktop `bin/` and `obj/` content, so builds can create unrelated modifications. Do not stage generated artifacts or unrelated user changes.