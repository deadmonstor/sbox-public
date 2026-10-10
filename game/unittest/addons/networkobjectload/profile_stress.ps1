param(
    [Parameter(Mandatory)][int]$TargetProcessId,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$stressIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$stressPrincipal = [Security.Principal.WindowsPrincipal]::new($stressIdentity)
if (-not $stressPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the capture helper elevated, after Josh approves the normal UAC prompt.'
}
$profiler = 'C:\Program Files\Superluminal\Performance\SuperluminalCmd.exe'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$target = Get-Process -Id $TargetProcessId
$targetStarted = $target.StartTime
@{ processId = $TargetProcessId; processName = $target.ProcessName; startUtc = $targetStarted.ToUniversalTime().ToString('o'); profilerVersion = (Get-Item -LiteralPath $profiler).VersionInfo.FileVersion } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ready.json')
try {
    $trigger = Join-Path $OutputDirectory 'trigger.json'
    $deadline = [DateTime]::UtcNow.AddMinutes(40)
    while (-not (Test-Path -LiteralPath $trigger)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Timed out waiting for diagnostic workload.' }
        Start-Sleep -Milliseconds 500
    }
    if ((Get-Process -Id $TargetProcessId).StartTime -ne $targetStarted) { throw 'Target process restarted.' }
    $capture = Join-Path $OutputDirectory 'capture.etl'
    & $profiler attach windows --process-id $TargetProcessId --max-duration 15 --freq 1000 --capture-path $capture 2>&1 |
        Tee-Object -FilePath (Join-Path $OutputDirectory 'capture.log')
    $captureExitCode = $LASTEXITCODE
    if ($captureExitCode -ne 0 -or -not (Test-Path -LiteralPath $capture) -or (Get-Item -LiteralPath $capture).Length -eq 0) {
        throw "Capture failed with exit code $captureExitCode"
    }
    @{ status = 'captured'; processId = $TargetProcessId; capture = $capture; durationSeconds = 15; frequencyHz = 1000 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'finished.json')
} catch {
    @{ status = 'failed'; error = $_.Exception.Message } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'finished.json')
    exit 1
}
