# WPF UI smoke test

Run from the repository root on Windows with .NET 10 SDK and an available desktop session:

```powershell
dotnet build tests/ThunderPack.UiSmoke/ThunderPack.UiSmoke.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet run --project tests/ThunderPack.UiSmoke/ThunderPack.UiSmoke.csproj -c Release --no-build -- .
```

The executable opens the actual application window with three deterministic local ZIP fixtures. It verifies the startup import guard, startup import, game-neutral defaults, editable game label, explicit universal/BepInEx layout selection, default text visibility, search, prerequisite filtering, selected-package details, the minimum 1120×760 layout, checkbox automation and two-way binding, export format controls, removal, clearing, and preservation of distinct known/unknown authors for same-name packages. It never downloads or executes a Mod.

Fixtures and the isolated workspace are created under `artifacts/ui-check-state/`. A 30-second watchdog terminates the test process if a modal error or dispatcher stall prevents completion. Exit code 0 means all thirteen checks passed; 1 indicates a failed assertion; 2 indicates watchdog timeout.

This test is separate from `build.ps1` because it requires a Windows desktop session.
