param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$Standalone
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$standaloneOut = Join-Path $root "dist-standalone"
$appProject = Join-Path $root "src\OpenCodeWarp.App\OpenCodeWarp.App.csproj"
$cliProject = Join-Path $root "src\OpenCodeWarp.Cli\OpenCodeWarp.Cli.csproj"

if ($Standalone) {
    # One portable exe with the .NET runtime bundled. Recipients run it without
    # installing the .NET 8 Desktop Runtime first.
    Write-Host "Building the standalone OpenCodeWarp.exe (bundles the .NET runtime; large file)..." -ForegroundColor Cyan
    dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -o $standaloneOut
    if ($LASTEXITCODE -ne 0) { throw "Standalone publish failed." }

    Write-Host ""
    Write-Host "Done. Output:" -ForegroundColor Green
    Get-ChildItem $standaloneOut | Select-Object Name, Length | Format-Table -AutoSize
    Write-Host "Send $standaloneOut\OpenCodeWarp.exe directly - no .NET runtime install needed."
    return
}

Write-Host "Building OpenCode Warp..." -ForegroundColor Cyan
if (Get-Process OpenCodeWarp -ErrorAction SilentlyContinue) {
    Write-Warning "OpenCode Warp is running. Close it before rebuilding so the files in dist can be replaced."
}
dotnet build (Join-Path $root "OpenCodeWarp.sln") -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Path $dist | Out-Null

Write-Host "Publishing the Windows dashboard..." -ForegroundColor Cyan
dotnet publish $appProject -c $Configuration -r $Runtime --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $dist
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

Write-Host "Publishing the ocwarp CLI..." -ForegroundColor Cyan
dotnet publish $cliProject -c $Configuration -r $Runtime --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $dist
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }

Write-Host ""
Write-Host "Done. Output:" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Start the dashboard with: $dist\OpenCodeWarp.exe"
