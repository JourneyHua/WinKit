# NpmProbe

Runs the npm service against deterministic fixture responses. The probe injects a fake command runner; it never starts Node/npm or performs real package install, update, or uninstall operations.

Run from the WinKit repository root:

```powershell
 dotnet run --project tools/NpmProbe/NpmProbe.csproj -c Release
```

The project links the production NpmService source directly and has no external package dependencies.
