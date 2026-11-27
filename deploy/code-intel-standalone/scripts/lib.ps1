# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
# Shared by the scripts here: run from anywhere, read .env without executing it.
$ErrorActionPreference = 'Stop'
$StackDir = Split-Path -Parent $PSScriptRoot
Set-Location $StackDir
if (-not (Test-Path .env)) { Write-Error "no .env in ${StackDir}: copy env-samples\<one>.env to .env first"; exit 2 }
$EnvFile = @{}
foreach ($line in Get-Content .env) {
    if ($line -match '^([A-Z0-9_]+)=(.*)$') { $EnvFile[$Matches[1]] = $Matches[2] }
}
function EnvGet([string]$Key, [string]$Default = '') {
    if ($EnvFile.ContainsKey($Key) -and $EnvFile[$Key]) { return $EnvFile[$Key] } else { return $Default }
}
$script:Failed = 0
function Ok([string]$Text) { Write-Host '  PASS ' -ForegroundColor Green -NoNewline; Write-Host $Text }
function Bad([string]$Text) { Write-Host '  FAIL ' -ForegroundColor Red -NoNewline; Write-Host $Text; $script:Failed++ }
