#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory = $false)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [switch]$SkipChecklist
)

$ErrorActionPreference = "Stop"

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host (" {0}" -f $Message) -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Checklist {
    Write-Host "[Checklist]" -ForegroundColor Yellow
    Write-Host "  - Update TradingBot.csproj version"
    Write-Host "  - Update CHANGELOG.md"
    Write-Host "  - Commit and push changes"
    Write-Host "  - Verify release build"
    Write-Host ""
}

Write-Header "TradingBot Release Helper"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-Host "Enter release version (ex: 2.3.2)"
    if ([string]::IsNullOrWhiteSpace($Version)) {
        Write-Host "Version is empty. Abort." -ForegroundColor Red
        exit 1
    }
}

Write-Host ("Release version: v{0}" -f $Version) -ForegroundColor Green
Write-Host ""

if (-not $SkipChecklist) {
    Write-Checklist

    $checklistPath = Join-Path $PSScriptRoot "RELEASE_CHECKLIST.md"
    if (Test-Path $checklistPath) {
        $openChecklist = Read-Host "Open RELEASE_CHECKLIST.md? [Y/N]"
        if ($openChecklist -eq "Y" -or $openChecklist -eq "y") {
            if (Get-Command code -ErrorAction SilentlyContinue) {
                code $checklistPath
            }
            else {
                Start-Process $checklistPath
            }
        }
    }
}

Write-Header "Deployment Commands"

$publishCmd = 'dotnet publish TradingBot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "bin\publish\win-x64"'
$packCmd = '.\publish-and-release.ps1 -PublishPath "bin\publish\win-x64" -Version "{0}"' -f $Version
$uploadCmd = 'gh release upload v{0} "Releases\TradingBot-win-Setup.exe" "Releases\TradingBot-{0}-full.nupkg" "Releases\TradingBot-win-Portable.zip" "Releases\RELEASES" "Releases\releases.win.json" "Releases\assets.win.json" --clobber' -f $Version
$latestCmd = 'gh release edit v{0} --latest' -f $Version
$tagCmd = 'git tag -a v{0} -m "Release v{0}"; git push Tradingbot v{0}' -f $Version

Write-Host ("1) {0}" -f $publishCmd) -ForegroundColor White
Write-Host ("2) {0}" -f $packCmd) -ForegroundColor White
Write-Host ("3) {0}" -f $uploadCmd) -ForegroundColor White
Write-Host ("4) {0}" -f $latestCmd) -ForegroundColor White
Write-Host ("5) {0}" -f $tagCmd) -ForegroundColor White
Write-Host ""

$oneLiner = '$v="{0}"; {1}; {2}; {3}; {4}' -f $Version, $publishCmd, $packCmd, $uploadCmd, $latestCmd
Write-Host "One-liner:" -ForegroundColor Yellow
Write-Host $oneLiner -ForegroundColor Gray
Write-Host ""

$copy = Read-Host "Copy one-liner to clipboard? [Y/N]"
if ($copy -eq "Y" -or $copy -eq "y") {
    $oneLiner | Set-Clipboard
    Write-Host "Copied to clipboard." -ForegroundColor Green
}

Write-Host "start-release completed." -ForegroundColor Green
