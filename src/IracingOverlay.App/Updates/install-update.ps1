# Installs a staged update after the overlay has exited, then starts it again.
# Started by UpdateService.StartInstaller; must stay compatible with Windows PowerShell 5.1 and ASCII-only.
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ResultFile,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
$logFile = [IO.Path]::ChangeExtension($ResultFile, '.log')

function Write-Log([string]$Message) {
    try { Add-Content -LiteralPath $logFile -Value ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message) -Encoding UTF8 } catch { }
}

# Antivirus scanners and the exiting process can hold a file for a moment; retry instead of failing.
function Invoke-WithRetry([scriptblock]$Action) {
    for ($attempt = 1; ; $attempt++) {
        try { & $Action; return }
        catch {
            if ($attempt -ge 20) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

$Source = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
$exePath = Join-Path $Target $Executable
$backup = Join-Path $Target ('.update-backup-' + [Guid]::NewGuid().ToString('N'))
$replaced = New-Object System.Collections.Generic.List[string]
$added = New-Object System.Collections.Generic.List[string]
$result = $null

Write-Log "Installing $Version from $Source into $Target"
try {
    # 1. Wait for the overlay to exit (it saves its settings on the way out).
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq [IO.Path]::GetFileNameWithoutExtension($Executable)) {
        if (-not $process.WaitForExit(30000)) {
            Write-Log 'The overlay did not exit within 30 seconds; stopping it.'
            Stop-Process -Id $ProcessId -Force
            [void]$process.WaitForExit(10000)
        }
    }

    # 2. Replace each file, keeping the old one in a backup folder for rollback.
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\')
        $destination = Join-Path $Target $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $saved = Join-Path $backup $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))
            Invoke-WithRetry { Move-Item -LiteralPath $destination -Destination $saved -Force }
            $replaced.Add($relative)
        }
        else {
            $added.Add($relative)
        }
        Invoke-WithRetry { Copy-Item -LiteralPath $file.FullName -Destination $destination -Force }
    }
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw "The update did not contain $Executable." }
    $result = "ok`r`n$Version"
    Write-Log "Installed $Version"
}
catch {
    $message = $_.Exception.Message
    Write-Log "Install failed: $message"
    # Roll back: remove files this update added and put the replaced ones back.
    foreach ($relative in $added) {
        try { Remove-Item -LiteralPath (Join-Path $Target $relative) -Force -ErrorAction SilentlyContinue } catch { }
    }
    foreach ($relative in $replaced) {
        try {
            $destination = Join-Path $Target $relative
            Invoke-WithRetry { Move-Item -LiteralPath (Join-Path $backup $relative) -Destination $destination -Force }
        }
        catch { Write-Log "Could not restore ${relative}: $($_.Exception.Message)" }
    }
    $result = "failed`r`n$message"
}
finally {
    try { Set-Content -LiteralPath $ResultFile -Value $result -Encoding UTF8 } catch { Write-Log "Could not write result: $($_.Exception.Message)" }
    foreach ($folder in @($backup, $Source)) {
        if (Test-Path -LiteralPath $folder) {
            try { Remove-Item -LiteralPath $folder -Recurse -Force } catch { Write-Log "Could not remove ${folder}: $($_.Exception.Message)" }
        }
    }
    # 3. Start the overlay again (the new version, or the restored old one after a failure).
    try {
        if ($Elevated) {
            # Started through Explorer so the overlay does not inherit administrator rights.
            Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $exePath + '"')
        }
        else {
            Start-Process -FilePath $exePath -WorkingDirectory $Target
        }
    }
    catch { Write-Log "Could not restart the overlay: $($_.Exception.Message)" }
}
