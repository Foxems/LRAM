<#
  Builds Lantern.
    .\build.ps1               build into dist\Lantern
    .\build.ps1 -Zip          also make dist\Lantern-<version>-win-x64.zip
    .\build.ps1 -Test         also build and run the self-tests (-Offline skips the few that talk to Roblox)
    .\build.ps1 -Output <dir> build somewhere else
    .\build.ps1 -Dotnet <exe> use this dotnet.exe instead of looking for one

  Uses the .NET SDK in .tools\dotnet if there is one, otherwise any `dotnet` on PATH, otherwise downloads a private copy into
  .tools\dotnet (no system-wide install, no admin rights). The app itself needs none of that: it runs on .NET Framework 4.8.
#>
param(
    [switch]$Zip,
    [switch]$Test,
    [switch]$Offline,
    [string]$Output,
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$root = $PSScriptRoot

$dotnet = if ($Dotnet) { $Dotnet } else { Join-Path $root '.tools\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $dotnet = $onPath.Source }
    else {
        Write-Host 'No .NET SDK found, downloading a private copy to .tools\dotnet ...'
        $script = Join-Path ([IO.Path]::GetTempPath()) 'dotnet-install.ps1'
        Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1 -OutFile $script
        & $script -Channel 8.0 -InstallDir (Join-Path $root '.tools\dotnet') -NoPath
        $dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
    }
}

$proj = Join-Path $root 'src\Lantern\Lantern.csproj'
$dist = if ($Output) { $Output } else { Join-Path $root 'dist\Lantern' }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

& $dotnet publish $proj -c Release -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# The app never loads these.
Get-ChildItem $dist -Include *.pdb, Microsoft.Web.WebView2.WinForms.dll -Recurse | Remove-Item -Force

# The licenses travel with the binaries (WebView2's license asks for it).
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.txt') { Copy-Item (Join-Path $root $doc) $dist -Force }

$size = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("`nBuilt: {0}  ({1:N1} MB on disk)" -f $dist, $size)

if ($Zip) {
    $version = (Get-Item (Join-Path $dist 'Lantern.exe')).VersionInfo.ProductVersion
    $zipPath = Join-Path (Split-Path $dist -Parent) ("Lantern-{0}-win-x64.zip" -f $version)
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zipPath
    Write-Host ("Zip:   {0}  ({1:N1} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
}

if ($Test) {
    $tests = Join-Path $root 'tests\Selftest\Selftest.csproj'
    & $dotnet build $tests -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The self-tests did not build.' }
    $exe = Join-Path $root 'tests\Selftest\bin\Release\net48\Selftest.exe'
    if ($Offline) { & $exe --offline } else { & $exe }
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
}
