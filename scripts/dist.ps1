# Run with Windows PowerShell or PowerShell 7 and the .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\IracingOverlay.App\IracingOverlay.App.csproj'
$dist = [IO.Directory]::CreateDirectory((Join-Path $root 'dist')).FullName
$buildId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $dist "publish-$buildId"
$pendingZip = Join-Path $dist "$buildId.zip"
$releaseZip = Join-Path $dist 'IracingOverlay-win-x64.zip'

try {
    # A fresh publish directory keeps stale files from earlier builds out of the ZIP.
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    if (!(Test-Path -LiteralPath (Join-Path $stage 'IracingOverlay.exe') -PathType Leaf)) {
        throw 'Publish did not produce IracingOverlay.exe.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $pendingZip)
    Move-Item -LiteralPath $pendingZip -Destination $releaseZip -Force
    Write-Output "Created $releaseZip"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $published = Get-Item -LiteralPath $stage
        if ($published.Parent.FullName -eq $dist -and $published.Name -eq "publish-$buildId") {
            Remove-Item -LiteralPath $published.FullName -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath $pendingZip) { Remove-Item -LiteralPath $pendingZip -Force }
}
