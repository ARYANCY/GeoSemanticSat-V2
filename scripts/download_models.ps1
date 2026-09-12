# PowerShell script to download and stage Qwen3-8B and Geospatial Foundation Models
# Can be run directly from the repository root:
# .\scripts\download_models.ps1 [-InstallTools] [-DownloadMethod <hf|git|pointers>]

param (
    [switch]$InstallTools,
    [string]$DownloadMethod = "pointers" # Options: hf, git, pointers
)

$ErrorActionPreference = "Continue"

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host " UpaGraha-V2 / GeoSemanticSat — AI & Foundation Models Setup" -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

if ($InstallTools) {
    Write-Host "`n[Step 1/2] Installing required CLI utilities (git-xet & hf)..." -ForegroundColor Yellow
    
    # 1. Install git-xet
    Write-Host "Installing git-xet via winget..." -ForegroundColor Gray
    winget install git-xet --accept-source-agreements --accept-package-agreements

    # 2. Install hf CLI
    Write-Host "Installing Hugging Face CLI (hf)..." -ForegroundColor Gray
    powershell -ExecutionPolicy ByPass -c "irm https://hf.co/cli/install.ps1 | iex"
}

Write-Host "`n[Step 2/2] Staging Foundation Models using method: $DownloadMethod" -ForegroundColor Yellow

$ModelsDir = Join-Path $PSScriptRoot "..\models"
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

$Models = @(
    @{ Name = "Qwen3-8B"; Repo = "Qwen/Qwen3-8B"; Url = "https://huggingface.co/Qwen/Qwen3-8B"; Target = "qwen3_8b" },
    @{ Name = "TerraMind-1.0-base"; Repo = "ibm-esa-geospatial/TerraMind-1.0-base"; Url = "https://huggingface.co/ibm-esa-geospatial/TerraMind-1.0-base"; Target = "terramind" },
    @{ Name = "SATMAE-PP-transformers"; Repo = "BiliSakura/SATMAE-PP-transformers"; Url = "https://huggingface.co/BiliSakura/SATMAE-PP-transformers"; Target = "satmae_pp" },
    @{ Name = "GFM_Composition_Pretraining"; Repo = "05kashyap/GFM_Composition_Pretraining"; Url = "https://github.com/05kashyap/GFM_Composition_Pretraining.git"; Target = "gfm_composition" },
    @{ Name = "Prithvi-EO-2.0-300M"; Repo = "ibm-nasa-geospatial/Prithvi-EO-2.0-300M"; Url = "https://huggingface.co/ibm-nasa-geospatial/Prithvi-EO-2.0-300M"; Target = "prithvi" }
)

foreach ($m in $Models) {
    $targetPath = Join-Path $ModelsDir $m.Target
    Write-Host "`n>>> Processing [$($m.Name)] -> $targetPath" -ForegroundColor Green
    
    if (Test-Path $targetPath) {
        Write-Host "Directory already exists: $targetPath" -ForegroundColor DarkGray
    }

    if ($DownloadMethod -eq "hf") {
        if ($m.Url -match "github.com") {
            git clone $m.Url $targetPath
        } else {
            hf download $m.Repo --local-dir $targetPath
        }
    }
    elseif ($DownloadMethod -eq "git") {
        git clone $m.Url $targetPath
    }
    elseif ($DownloadMethod -eq "pointers") {
        if ($m.Url -match "github.com") {
            git clone $m.Url $targetPath
        } else {
            $env:GIT_LFS_SKIP_SMUDGE = "1"
            git clone $m.Url $targetPath
            $env:GIT_LFS_SKIP_SMUDGE = "0"
        }
    }
}

Write-Host "`nVerifying model inventory status..." -ForegroundColor Yellow
python (Join-Path $PSScriptRoot "download_models.py") --method status-only

Write-Host "`nSetup completed!" -ForegroundColor Cyan
