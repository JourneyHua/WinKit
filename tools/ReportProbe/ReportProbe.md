# Report session probe

The probe compiles the current ReportViewModel source and references an already built WinKit.dll for its other types and the unchanged Markdown/JSON serializers. It has no ProjectReference and never builds or overwrites the main application. Build and runtime output stay under tools/ReportProbe/.

From the WinKit directory, with .NET SDK 10 on Windows:

```powershell
dotnet build tools/ReportProbe/ReportProbe.csproj --configuration Release --nologo
dotnet tools/ReportProbe/bin/Release/net10.0-windows/ReportProbe.dll | Tee-Object -FilePath tools/ReportProbe/results.json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

First build the main application in Release. The default reference is src/WinKit/bin/Release/net10.0-windows/WinKit.dll. To use a different existing assembly directory, pass -p:WinKitAssemblyDir="C:/absolute/path/to/assembly-directory" to the build command. That directory must contain WinKit.dll; an absent reference fails without triggering a main-project build.

The process runs an STA WPF dispatcher. Internal constructor delegates supply deterministic report/status reads; both execute through the production Task.Run paths. No real report collection, maintenance creation/deletion/run, settings writes, report exports to the user profile, or UI confirmation dialogs are triggered. The only real machine check is the existing read-only elevation check in the view-model constructor.

Checks cover:

- The initial page call returns while blocked collectors execute off the UI thread. Ten further entries share the in-flight requests; ten entries after completion preserve the report object, sections, generation time, and collection bindings. Total collection/status read counts stay at one each.
- GenerateCommand performs exactly one new report collection and maintenance lookup. Navigation and repeated command attempts during the in-flight operation do not duplicate work. The new report and generation time replace the old snapshot, both export commands remain usable, and the original Markdown/JSON serializers contain the new data/time.
- An unknown maintenance result is cached too. RefreshTaskCommand explicitly re-reads it and refreshes the command state without collecting another report.
- A failed recheck preserves the previous report, its timestamp, the error, and export availability. A later explicit recheck recovers. A failed initial collection retries on re-entry while the completed maintenance lookup is retained.
- A second view-model instance collects independently, demonstrating the cache is instance/session scoped.
- The existing five-second maintenance observer continues after simulated navigation and avoids duplicate status reads. The explicit-action wrapper is exercised with a harmless delegate: it re-reads state, balances busy callbacks, and the existing completion observer notices the changed last-run time.

Successful execution emits one JSON document with passed=8 and per-case evidence. The two observer cases take about ten seconds combined. Exit code is nonzero on failure.

The probe verifies export command availability and the actual serializers, but intentionally does not execute ExportAsync's user-profile file write or real Windows maintenance mutations. Those implementation paths are unchanged by this task. HealthReportService remains uncached, so direct and scheduled report builds still collect afresh.
