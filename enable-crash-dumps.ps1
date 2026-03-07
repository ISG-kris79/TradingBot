#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Configure TradingBot.exe crash dump collection with WER LocalDumps.

.DESCRIPTION
    - Enable: create LocalDumps config for TradingBot.exe
    - Disable: remove LocalDumps config for TradingBot.exe
    - Status: show config and dump file status

.EXAMPLE
    .\enable-crash-dumps.ps1 -Mode Enable

.EXAMPLE
    .\enable-crash-dumps.ps1 -Mode Status

.EXAMPLE
    .\enable-crash-dumps.ps1 -Mode Disable
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Enable", "Disable", "Status")]
    [string]$Mode = "Enable",

    [Parameter(Mandatory = $false)]
    [string]$ProcessName = "TradingBot.exe",

    [Parameter(Mandatory = $false)]
    [string]$DumpFolder = "$env:LOCALAPPDATA\TradingBot\CrashDumps",

    [Parameter(Mandatory = $false)]
    [ValidateSet(1, 2)]
    [int]$DumpType = 2,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 100)]
    [int]$DumpCount = 10
)

$ErrorActionPreference = "Stop"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Header {
    param([string]$Title)
    Write-Host ""
    Write-Host "====================================================" -ForegroundColor Cyan
    Write-Host " $Title" -ForegroundColor Cyan
    Write-Host "====================================================" -ForegroundColor Cyan
}

$baseRegPath = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps"
$targetRegPath = Join-Path $baseRegPath $ProcessName

switch ($Mode) {
    "Enable" {
        if (-not (Test-Admin)) {
            throw "Administrator privileges are required. Re-run PowerShell as Administrator."
        }

        Write-Header "Enable TradingBot crash dumps"

        if (-not (Test-Path $DumpFolder)) {
            New-Item -Path $DumpFolder -ItemType Directory -Force | Out-Null
            Write-Host "Created dump folder: $DumpFolder" -ForegroundColor Green
        }
        else {
            Write-Host "Dump folder exists: $DumpFolder" -ForegroundColor Green
        }

        if (-not (Test-Path $targetRegPath)) {
            New-Item -Path $targetRegPath -Force | Out-Null
        }

        New-ItemProperty -Path $targetRegPath -Name "DumpFolder" -PropertyType ExpandString -Value $DumpFolder -Force | Out-Null
        New-ItemProperty -Path $targetRegPath -Name "DumpType" -PropertyType DWord -Value $DumpType -Force | Out-Null
        New-ItemProperty -Path $targetRegPath -Name "DumpCount" -PropertyType DWord -Value $DumpCount -Force | Out-Null

        Write-Host "Registry configured: $targetRegPath" -ForegroundColor Green
        Write-Host (" - DumpFolder: {0}" -f $DumpFolder)
        Write-Host (" - DumpType: {0} (2=Full, 1=Mini)" -f $DumpType)
        Write-Host (" - DumpCount: {0}" -f $DumpCount)
        Write-Host ""
        Write-Host "Crash dumps will be created on next crash." -ForegroundColor Yellow
        break
    }

    "Disable" {
        if (-not (Test-Admin)) {
            throw "Administrator privileges are required. Re-run PowerShell as Administrator."
        }

        Write-Header "Disable TradingBot crash dumps"

        if (Test-Path $targetRegPath) {
            Remove-Item -Path $targetRegPath -Recurse -Force
            Write-Host "Registry setting removed: $targetRegPath" -ForegroundColor Green
        }
        else {
            Write-Host "No setting found to remove: $targetRegPath" -ForegroundColor Yellow
        }
        break
    }

    "Status" {
        Write-Header "TradingBot crash dump status"

        if (Test-Path $targetRegPath) {
            $props = Get-ItemProperty -Path $targetRegPath
            Write-Host "LocalDumps status: enabled" -ForegroundColor Green
            Write-Host " - Process: $ProcessName"
            Write-Host " - DumpFolder: $($props.DumpFolder)"
            Write-Host " - DumpType: $($props.DumpType)"
            Write-Host " - DumpCount: $($props.DumpCount)"

            $folder = [Environment]::ExpandEnvironmentVariables([string]$props.DumpFolder)
            if (Test-Path $folder) {
                $dumps = Get-ChildItem -Path $folder -Filter "*.dmp" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
                Write-Host ""
                Write-Host "Dump files: $($dumps.Count)"
                if ($dumps.Count -gt 0) {
                    $latest = $dumps | Select-Object -First 1
                    Write-Host "Latest dump: $($latest.FullName)" -ForegroundColor Cyan
                    Write-Host "Created at: $($latest.LastWriteTime)" -ForegroundColor Cyan
                }
            }
            else {
                Write-Host "Dump folder does not exist: $folder" -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "LocalDumps status: disabled" -ForegroundColor Yellow
            Write-Host "Enable with: .\enable-crash-dumps.ps1 -Mode Enable" -ForegroundColor Gray
        }
        break
    }
}
