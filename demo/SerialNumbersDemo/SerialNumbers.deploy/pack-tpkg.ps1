#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds SerialNumbers.Core and packs it into SerialNumbers.tpkg for a Tsak worker.

.DESCRIPTION
    The package holds the manifest, the module config and the two DLLs of the demo:
    SerialNumbers.Core.dll (the entry point) and SerialNumbers.Domain.dll. redb, redb.Route and
    their connectors already ship in the worker's shared libraries.

.EXAMPLE
    ./pack-tpkg.ps1
    ./pack-tpkg.ps1 -Configuration Debug
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$core = Join-Path $root "SerialNumbers.Core"
$bin = Join-Path $core "bin/$Configuration/net10.0"
$staging = Join-Path ([IO.Path]::GetTempPath()) "serialnumbers-tpkg"
$output = Join-Path $PSScriptRoot "output"
$package = Join-Path $output "SerialNumbers.tpkg"

dotnet build (Join-Path $core "SerialNumbers.Core.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build of SerialNumbers.Core failed." }

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force $staging | Out-Null
New-Item -ItemType Directory -Force $output | Out-Null

Copy-Item (Join-Path $core "manifest.json") $staging
Copy-Item (Join-Path $core "SerialNumbers.Core.config.json") $staging
Copy-Item (Join-Path $bin "SerialNumbers.Core.dll") $staging
Copy-Item (Join-Path $bin "SerialNumbers.Domain.dll") $staging

if (Test-Path $package) { Remove-Item -Force $package }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $package)

Write-Host "Packed $package"
