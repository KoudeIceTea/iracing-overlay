# Publishes a release that the app's update check picks up.
#   1. Bump <Version> in src/IracingOverlay.App/IracingOverlay.App.csproj and commit.
#   2. Run:  powershell -ExecutionPolicy Bypass -File scripts/release.ps1 [-Notes "What changed"]
# Builds the ZIP (scripts/dist.ps1), pushes main and creates GitHub release v<Version> with the ZIP attached.
# Needs the GitHub CLI (gh) logged in with access to the repository.
param([string]$Notes = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'KoudeIceTea/iracing-overlay'
$csproj = Join-Path $root 'src\IracingOverlay.App\IracingOverlay.App.csproj'

$version = ([xml](Get-Content -LiteralPath $csproj -Raw)).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $csproj." }
$tag = "v$version"

if (git -C $root status --porcelain) { throw 'There are uncommitted changes: commit them first, so the release matches the code.' }
if (git -C $root tag --list $tag) { throw "$tag already exists: bump <Version> in IracingOverlay.App.csproj first." }

& (Join-Path $PSScriptRoot 'dist.ps1')
$zip = Join-Path $root 'dist\IracingOverlay-win-x64.zip'
if (-not (Test-Path -LiteralPath $zip)) { throw "Build did not produce $zip." }

git -C $root push origin main
if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }

$arguments = @('release', 'create', $tag, $zip, '--repo', $repo, '--title', $tag, '--target', 'main')
if ($Notes) { $arguments += @('--notes', $Notes) } else { $arguments += '--generate-notes' }
gh @arguments
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
Write-Output "Released $tag. Apps with a token will see it on their next check (start-up or hourly)."
