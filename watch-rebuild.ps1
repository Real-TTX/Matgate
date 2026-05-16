$root = Get-Location
$exclusions = @('.git', 'bin', 'obj', '.vs', 'data')
$script:buildRunning = $false

function IsExcludedPath($path) {
    foreach ($exclude in $exclusions) {
        if ($path -like "*$exclude*") {
            return $true
        }
    }
    return $false
}

function Invoke-Rebuild {
    if ($script:buildRunning) {
        return
    }

    $script:buildRunning = $true
    try {
        Write-Host "[watch-rebuild] Change detected. Rebuilding Matgate Docker image..."
        docker compose build --no-cache matgate
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[watch-rebuild] Build complete. Restarting matgate service..."
            docker compose up -d --force-recreate --build matgate
        } else {
            Write-Host "[watch-rebuild] Build failed. Fix errors and save again."
        }
    } catch {
        Write-Host "[watch-rebuild] Fehler: $_"
    } finally {
        $script:buildRunning = $false
    }
}

$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = $root.Path
$watcher.IncludeSubdirectories = $true
$watcher.Filter = '*.*'
$watcher.NotifyFilter = [System.IO.NotifyFilters]::FileName -bor [System.IO.NotifyFilters]::LastWrite -bor [System.IO.NotifyFilters]::DirectoryName

$debounceTimer = New-Object System.Timers.Timer 800
$debounceTimer.AutoReset = $false
$debounceTimer.Add_Elapsed({ Invoke-Rebuild })

$action = {
    param($sender, $eventArgs)
    $path = $eventArgs.FullPath
    if (IsExcludedPath $path) { return }
    $debounceTimer.Stop()
    $debounceTimer.Start()
}

Register-ObjectEvent $watcher Changed -SourceIdentifier WatcherChanged -Action $action | Out-Null
Register-ObjectEvent $watcher Created -SourceIdentifier WatcherCreated -Action $action | Out-Null
Register-ObjectEvent $watcher Renamed -SourceIdentifier WatcherRenamed -Action $action | Out-Null
Register-ObjectEvent $watcher Deleted -SourceIdentifier WatcherDeleted -Action $action | Out-Null

$watcher.EnableRaisingEvents = $true
Write-Host "[watch-rebuild] Watching Matgate sources for changes... Press Ctrl+C to stop."

while ($true) {
    Start-Sleep -Seconds 1
}
