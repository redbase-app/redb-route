#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds SerialNumbers.Core and packs it into SerialNumbers.tpkg for a Tsak worker.

.DESCRIPTION
    The package holds the manifest, the module config, the two DLLs of the demo
    (SerialNumbers.Core.dll, the entry point, and SerialNumbers.Domain.dll) and EF Core, which the
    module uses for the product status journal. redb, redb.Route, their connectors and
    Microsoft.Data.SqlClient already ship in the worker's shared libraries, so they stay out of the
    package and EF Core runs on the worker's SqlClient, the one redb uses.

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
$publish = Join-Path ([IO.Path]::GetTempPath()) "serialnumbers-publish"
$staging = Join-Path ([IO.Path]::GetTempPath()) "serialnumbers-tpkg"
$output = Join-Path $PSScriptRoot "output"
$package = Join-Path $output "SerialNumbers.tpkg"

# publish, not build: a class library's build output does not contain its NuGet dependencies.
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $core "SerialNumbers.Core.csproj") -c $Configuration -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish of SerialNumbers.Core failed." }

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force $staging | Out-Null
New-Item -ItemType Directory -Force $output | Out-Null

Copy-Item (Join-Path $core "manifest.json") $staging
Copy-Item (Join-Path $core "SerialNumbers.Core.config.json") $staging
Copy-Item (Join-Path $publish "SerialNumbers.Core.dll") $staging
Copy-Item (Join-Path $publish "SerialNumbers.Domain.dll") $staging

$efCore = Get-ChildItem $publish -Filter "Microsoft.EntityFrameworkCore*.dll"
if ($efCore.Count -eq 0) { throw "EF Core assemblies were not found in $publish." }
$efCore | Copy-Item -Destination $staging

if (Test-Path $package) { Remove-Item -Force $package }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $package)

Write-Host "Packed $package"
