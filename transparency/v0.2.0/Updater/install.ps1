param([Parameter(Mandatory=$true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
function FileHash([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Inside([string]$Root,[string]$Relative) {
    $basePath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw 'Invalid update path.' }
    $targetPath = [IO.Path]::GetFullPath((Join-Path -Path $basePath -ChildPath $Relative))
    if (-not $targetPath.StartsWith($basePath + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escaped its folder.' }
    return $targetPath
}
function NoRedirect([string]$Path,[string]$Root) {
    $currentPath = $Path
    while ($currentPath.Length -ge $Root.Length) {
        if (Test-Path -LiteralPath $currentPath) {
            if (((Get-Item -LiteralPath $currentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A redirected installation path cannot be updated.' }
        }
        $currentPath = [IO.Path]::GetDirectoryName($currentPath)
        if (-not $currentPath) { break }
    }
}
$installRoot = [IO.Path]::GetFullPath($plan.installRoot).TrimEnd('\')
$workRoot = [IO.Path]::GetFullPath($plan.workRoot).TrimEnd('\')
$stageRoot = [IO.Path]::GetFullPath($plan.stageRoot).TrimEnd('\')
if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\') -or -not $stageRoot.StartsWith($workRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid update folders.' }
$jobRoot = [IO.Path]::GetDirectoryName($stageRoot)
$backupRoot = Inside $jobRoot 'backup'
$resultPath = Inside $workRoot 'result.json'
$pendingPath = Inside $workRoot 'pending.json'
$modified = New-Object 'System.Collections.Generic.List[object]'
$success = $false
$failure = ''
$updateMutex = $null
$ownsMutex = $false
try {
    if (@($plan.files.PSObject.Properties).Count -eq 0) { throw 'Empty update manifest.' }
    foreach ($entry in $plan.files.PSObject.Properties) {
        $sourcePath = Inside $stageRoot $entry.Name
        NoRedirect $sourcePath $stageRoot
        if ((FileHash $sourcePath) -ne $entry.Value) { throw 'Update checksum failed.' }
        $null = Inside $installRoot $entry.Name
    }
    if ($plan.parentId -gt 0) {
        $parentProcess = Get-Process -Id $plan.parentId -ErrorAction SilentlyContinue
        if ($parentProcess -and -not $parentProcess.WaitForExit(30000)) { throw 'AltSync did not close in time.' }
    }
    if ($plan.restart) {
        $updateMutex = New-Object Threading.Mutex($false,'Local\AltSync.Desktop')
        try { $ownsMutex = $updateMutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
        if (-not $ownsMutex) { throw 'Another AltSync window is still open.' }
    }
    if (Test-Path -LiteralPath $pendingPath) { Remove-Item -LiteralPath $pendingPath }
    foreach ($entry in $plan.files.PSObject.Properties) {
        $destination = Inside $installRoot $entry.Name
        $source = Inside $stageRoot $entry.Name
        $backup = Inside $backupRoot $entry.Name
        NoRedirect $destination $installRoot
        $existed = Test-Path -LiteralPath $destination
        if ($existed -and (Get-Item -LiteralPath $destination).PSIsContainer) { throw 'An installation file is a directory.' }
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        if ($existed) {
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))
            Copy-Item -LiteralPath $destination -Destination $backup
        }
        $temporary = Inside $installRoot ($entry.Name + '.altsync-' + [Guid]::NewGuid().ToString('N'))
        try {
            [IO.File]::Copy($source,$temporary,$false)
            if ($existed) { [IO.File]::Replace($temporary,$destination,[NullString]::Value) }
            else { [IO.File]::Move($temporary,$destination) }
            $modified.Add([pscustomobject]@{ Name=$entry.Name; Existed=$existed })
        } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary } }
    }
    $success = $true
} catch {
    $failure = $_.Exception.Message
    for ($index = $modified.Count - 1; $index -ge 0; $index--) {
        $entry = $modified[$index]
        $destination = Inside $installRoot $entry.Name
        if ($entry.Existed) { Copy-Item -LiteralPath (Inside $backupRoot $entry.Name) -Destination $destination -Force }
        elseif (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination }
    }
    if (Test-Path -LiteralPath $pendingPath) { Remove-Item -LiteralPath $pendingPath }
} finally {
    try { [pscustomobject]@{ success=$success; version=$plan.version; time=[DateTime]::UtcNow.ToString('o'); error=$failure } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8 } catch { }
    if ($ownsMutex) { $updateMutex.ReleaseMutex() }
    if ($updateMutex) { $updateMutex.Dispose() }
}
if ($plan.restart -and (Test-Path -LiteralPath (Inside $installRoot 'AltSync.exe') -PathType Leaf)) {
    Start-Process -FilePath (Inside $installRoot 'AltSync.exe') -WorkingDirectory $installRoot -WindowStyle Normal
}
if (-not $success) { exit 1 }
