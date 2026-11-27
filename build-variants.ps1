#Requires -Version 7.4
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist/variants'))

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Building and launching these variants requires Windows.' }

$projectPath = Join-Path $PSScriptRoot 'src/WinKit/WinKit.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid WinKit version.' }

$dotnet = Join-Path $env:USERPROFILE 'dotnet-sdk/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$variants = @(
    @{ Name = 'compressed'; SelfContained = 'true'; Compress = 'true' }
    @{ Name = 'framework-dependent'; SelfContained = 'false'; Compress = 'false' }
)
foreach ($variant in $variants) {
    $variant.FileName = "WinKit-v$version-win-x64-$($variant.Name).exe"
    $variant.Destination = Join-Path $output $variant.FileName
    if (Test-Path -LiteralPath $variant.Destination) { throw "Already exists: $($variant.Destination). Choose a fresh output directory." }
}

$scratch = Join-Path $output ('.build-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
try {
    foreach ($variant in $variants) {
        $publish = Join-Path $scratch $variant.Name
        & $dotnet publish $projectPath -c Release -r win-x64 -m:1 -t:Rebuild --nologo `
            --self-contained $variant.SelfContained -p:PublishSingleFile=true `
            "-p:IncludeNativeLibrariesForSelfExtract=$($variant.SelfContained)" `
            "-p:EnableCompressionInSingleFile=$($variant.Compress)" `
            -p:DebugType=embedded -p:PublishTrimmed=false -p:PublishAot=false -o $publish
        if ($LASTEXITCODE -ne 0) { throw "Publishing $($variant.Name) failed ($LASTEXITCODE)." }

        $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
        if ($files.Count -ne 1 -or $files[0].Name -cne 'WinKit.exe') { throw 'Expected exactly one WinKit.exe.' }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($files[0].FullName)
        if ($info.ProductVersion -cne $version -or $info.FileVersion -cne "$version.0") { throw 'Unexpected EXE version.' }

        # Test the actual delivery name. Motion smoke does not treat this EXE as the canonical updater asset.
        $variant.Staged = Join-Path $scratch $variant.FileName
        [IO.File]::Move($files[0].FullName, $variant.Staged)
        $smoke = Join-Path $scratch "$($variant.Name)-smoke"
        $environment = @{
            DOTNET_BUNDLE_EXTRACT_BASE_DIR = (Join-Path $scratch 'bundle')
            DOTNET_DISABLE_GUI_ERRORS = '1'
        }
        if ($variant.SelfContained -eq 'true') {
            # If the supposedly self-contained app consults a machine runtime, the empty root prevents fallback.
            $emptyRuntime = Join-Path $scratch 'empty-dotnet'
            [void][IO.Directory]::CreateDirectory($emptyRuntime)
            $environment.DOTNET_ROOT_X64 = $emptyRuntime
            $environment.DOTNET_ROOT = $emptyRuntime
        }
        $process = Start-Process -FilePath $variant.Staged -ArgumentList '--motion-smoke', "`"$smoke`"" `
            -Environment $environment -PassThru -Wait
        $log = Join-Path $smoke 'motion.log'
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $log) -or
            (Get-Content -LiteralPath $log -Raw) -notmatch 'ux-motion: .*result=OK') {
            throw "Motion smoke for $($variant.Name) failed ($($process.ExitCode))."
        }
    }

    foreach ($variant in $variants) {
        [IO.File]::Move($variant.Staged, $variant.Destination)
        $file = Get-Item -LiteralPath $variant.Destination
        $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        '{0}: {1} bytes, SHA-256 {2}' -f $file.FullName, $file.Length, $hash.Hash.ToLowerInvariant()
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
