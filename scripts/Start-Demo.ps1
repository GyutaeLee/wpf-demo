param(
    [ValidateSet('Basic', 'ConcurrentLoan', 'LostResponse', 'ApiDown', 'RestartRecovery', 'All')]
    [string]$Scenario = 'Basic',
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$apiAddress = 'http://127.0.0.1:5187'
$equipmentId = 1001
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $scriptParent = Split-Path $PSScriptRoot -Parent
    $OutputRoot = if (Test-Path (Join-Path $scriptParent 'api\WpfDemo.Api.exe')) {
        $scriptParent
    } else {
        Join-Path $scriptParent 'artifacts'
    }
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$apiExecutable = Join-Path $outputRoot 'api\WpfDemo.Api.exe'
$clientExecutable = Join-Path $outputRoot 'client\WpfDemo.exe'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public struct DemoWindowRect {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class DemoWindowApi {
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out DemoWindowRect rect);
}
'@

function Invoke-WinApp {
    param([Parameter(Mandatory)][AllowEmptyString()][string[]]$Arguments)
    $null = & winapp @Arguments
    if ($LASTEXITCODE -ne 0) { throw "WinAppCLI failed ($LASTEXITCODE): winapp $($Arguments -join ' ')" }
}

function Invoke-Ui {
    param(
        [Parameter(Mandatory)][string]$Verb,
        [Parameter(Mandatory)][int]$ProcessId,
        [string]$Selector,
        [string]$Value,
        [string]$OutputPath,
        [int]$TimeoutMs = 20000
    )
    $arguments = @('ui', $Verb)
    if ($Selector) { $arguments += $Selector }
    if ($PSBoundParameters.ContainsKey('Value')) { $arguments += $Value }
    $arguments += @('-a', [string]$ProcessId)
    if ($Verb -eq 'wait-for') { $arguments += @('-t', [string]$TimeoutMs) }
    if ($OutputPath) { $arguments += @('--output', $OutputPath) }
    Invoke-WinApp -Arguments $arguments
}

function Wait-ForWindow {
    param([System.Diagnostics.Process]$Client)
    Invoke-Ui -Verb 'wait-for' -Selector 'Equipment1001' -ProcessId $Client.Id
    Invoke-Ui -Verb 'wait-for' -Selector 'BorrowButton' -ProcessId $Client.Id
}

function Start-Client {
    param([string]$Directory, [string]$Label)
    $client = Start-ClientProcess $Directory $Label
    try {
        Wait-ForWindow -Client $client
        $client.Refresh()
        if ($client.HasExited) { throw "Client $Label exited during startup." }
        return $client
    } catch {
        Stop-OwnedProcess $client
        throw
    }
}

function Start-ClientProcess {
    param([string]$Directory, [string]$Label)
    New-Item -ItemType Directory -Force $Directory | Out-Null
    $env:WPFDEMO_CLIENT_DATA_DIR = $Directory
    $env:WPFDEMO_CLIENT_LABEL = $Label
    $env:WPFDEMO_API_URL = $apiAddress
    return Start-Process -FilePath $clientExecutable -PassThru
}

function Assert-PortAvailable {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $listener = Get-NetTCPConnection -LocalPort 5187 -State Listen -ErrorAction SilentlyContinue
        if (-not $listener) { return }
        Start-Sleep -Milliseconds 250
    }
    throw 'Port 5187 is already in use. The demo did not stop or modify the existing process.'
}

function Start-Api {
    param([string]$DataDirectory, [bool]$DropNextBorrowResponse, [string]$LogDirectory)
    Assert-PortAvailable
    New-Item -ItemType Directory -Force $DataDirectory, $LogDirectory | Out-Null
    $env:WPFDEMO_SERVER_DATA_DIR = $DataDirectory
    $env:WPFDEMO_DEMO_DROP_NEXT_BORROW_RESPONSE = if ($DropNextBorrowResponse) { 'true' } else { 'false' }
    $stdout = Join-Path $LogDirectory 'api.stdout.log'
    $stderr = Join-Path $LogDirectory 'api.stderr.log'
    $process = Start-Process -FilePath $apiExecutable -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $process.Refresh()
            if ($process.HasExited) { throw "API exited before becoming ready (exit $($process.ExitCode))." }
            try {
                $response = Invoke-RestMethod "$apiAddress/api/equipment" -TimeoutSec 2
                if ($response.items.Count -eq 5) { return $process }
            } catch { Start-Sleep -Milliseconds 350 }
        }
        throw 'Local API did not become ready within 14 seconds.'
    } catch {
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        throw
    }
}

function Stop-OwnedProcess {
    param([System.Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    $Process.Refresh()
    if (-not $Process.HasExited) { Stop-Process -Id $Process.Id -Force }
    $Process.Dispose()
}

function Start-Recording {
    param([int]$ProcessId, [string]$Path)
    New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString()
    $recordingLog = "$Path.json"
    $recordingError = "$Path.stderr.log"
    $arguments = "ui record -a $ProcessId --duration-sec 30 --fps 8 --max-edge 1280 --frames --output `"$Path`" --json"
    $recorder = Start-Process -FilePath (Get-Command winapp).Source -ArgumentList $arguments -PassThru `
        -RedirectStandardOutput $recordingLog -RedirectStandardError $recordingError
    Start-Sleep -Seconds 2
    $recorder.Refresh()
    if ($recorder.HasExited -and $recorder.ExitCode -ne 0) {
        throw "WinAppCLI recording failed to start: $(Get-Content $recordingError -Raw)"
    }
    return $recorder
}

function Complete-Recording {
    param([System.Diagnostics.Process]$Recorder, [string]$Path)
    if ($null -eq $Recorder) { return }
    if (-not $Recorder.WaitForExit(60000)) {
        Stop-Process -Id $Recorder.Id -Force
        throw "WinAppCLI recording did not finish: $Path"
    }
    if ($Recorder.ExitCode -ne 0 -or -not (Test-Path $Path) -or (Get-Item $Path).Length -lt 10000) {
        throw "WinAppCLI did not produce a complete video: $Path"
    }
    $framesDirectory = [System.IO.Path]::ChangeExtension($Path, '.frames')
    $manifestPath = Join-Path $framesDirectory 'manifest.json'
    $frameLogPath = Join-Path $framesDirectory 'frames.ndjson'
    if (-not (Test-Path $manifestPath) -or -not (Test-Path $frameLogPath)) {
        throw "Video frame evidence is missing: $framesDirectory"
    }
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $frameEntries = @(Get-Content $frameLogPath | ForEach-Object { $_ | ConvertFrom-Json })
    $imageCount = @(Get-ChildItem (Join-Path $framesDirectory 'frames') -Filter '*.jpg' -ErrorAction SilentlyContinue).Count
    if ($manifest.status -ne 'complete' -or $frameEntries.Count -lt 3 -or $imageCount -lt 2 -or
        [long]$frameEntries[0].elapsedMs -gt 3000 -or [long]$frameEntries[-1].elapsedMs -lt 15000) {
        throw "Video evidence is incomplete or contains no visible state change: $framesDirectory"
    }
    $result = Get-Content "$Path.json" -Raw | ConvertFrom-Json
    if ($result.status -and $result.status -ne 'complete') { throw "Video status is $($result.status): $Path" }
    $Recorder.Dispose()
}

function Get-EquipmentState {
    $catalog = Invoke-RestMethod "$apiAddress/api/equipment" -TimeoutSec 5
    return $catalog.items | Where-Object id -eq $equipmentId | Select-Object -First 1
}

function Get-EquipmentHistory {
    return @(Invoke-RestMethod "$apiAddress/api/equipment/$equipmentId/history" -TimeoutSec 5)
}

function Assert-OperationCounts {
    param([int]$ExpectedBorrows, [int]$ExpectedReturns, [string]$ExpectedStatus)
    $item = Get-EquipmentState
    $history = Get-EquipmentHistory
    $borrowEvents = @($history | Where-Object kind -eq 'Borrowed')
    $returnEvents = @($history | Where-Object kind -eq 'Returned')
    if ($borrowEvents.Count -ne $ExpectedBorrows -or $returnEvents.Count -ne $ExpectedReturns -or $item.status -ne $ExpectedStatus) {
        throw "Unexpected server state: status=$($item.status), borrows=$($borrowEvents.Count), returns=$($returnEvents.Count)."
    }
    return [ordered]@{
        equipmentId = $equipmentId
        status = $item.status
        activeLoanId = if ($item.activeLoan) { $item.activeLoan.id } else { $null }
        borrowEvents = $borrowEvents.Count
        returnEvents = $returnEvents.Count
        historyOperationIds = @($history | Where-Object operationId | ForEach-Object operationId)
    }
}

function Select-Equipment {
    param([System.Diagnostics.Process]$Client)
    Invoke-Ui -Verb 'click' -Selector 'Equipment1001' -ProcessId $Client.Id
}

function Begin-Borrow {
    param([System.Diagnostics.Process]$Client, [string]$Note)
    Select-Equipment -Client $Client
    Invoke-Ui -Verb 'set-value' -Selector 'OperationNote' -Value $Note -ProcessId $Client.Id
    Invoke-Ui -Verb 'invoke' -Selector 'BorrowButton' -ProcessId $Client.Id
}

function Wait-OperationNotice {
    param([System.Diagnostics.Process]$Client, [string]$Text)
    Invoke-WinApp -Arguments @('ui', 'wait-for', 'OperationNotice', '-a', [string]$Client.Id, '--value', $Text, '-t', '30000')
}

function Save-Screenshot {
    param([System.Diagnostics.Process]$Client, [string]$Path)
    New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null
    Invoke-Ui -Verb 'screenshot' -ProcessId $Client.Id -OutputPath $Path
    if (-not (Test-Path $Path) -or (Get-Item $Path).Length -lt 1000) { throw "Screenshot was not captured: $Path" }
}

function Assert-VisibleInWindow {
    param([string]$Selector, [System.Diagnostics.Process]$Client)
    $result = & winapp ui get-property $Selector -a ([string]$Client.Id) --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect the '$Selector' control." }
    $Client.Refresh()
    $windowRect = New-Object DemoWindowRect
    if (-not [DemoWindowApi]::GetWindowRect($Client.MainWindowHandle, [ref]$windowRect)) {
        throw 'Could not read the WPF window bounds.'
    }
    $element = $result.element
    if ($element.isOffscreen -or $element.width -le 0 -or $element.height -le 0 -or
        $element.x -lt $windowRect.Left -or $element.y -lt $windowRect.Top -or
        ($element.x + $element.width) -gt $windowRect.Right -or
        ($element.y + $element.height) -gt $windowRect.Bottom) {
        throw "The '$Selector' control is outside the visible WPF window."
    }
}

function Test-EscapeCancelsDraft {
    param([System.Diagnostics.Process]$Client)
    Select-Equipment -Client $Client
    Invoke-Ui -Verb 'set-value' -Selector 'OperationNote' -Value 'discard this draft' -ProcessId $Client.Id
    Invoke-Ui -Verb 'focus' -Selector 'OperationNote' -ProcessId $Client.Id
    Invoke-WinApp -Arguments @('ui', 'send-keys', 'escape', '-a', [string]$Client.Id, '--via', 'send-input')
    Invoke-WinApp -Arguments @('ui', 'wait-for', 'OperationNote', '-a', [string]$Client.Id,
        '--property', 'Value', '--value', '', '-t', '3000')
}

function Test-TabOrder {
    param([System.Diagnostics.Process]$Client)
    Invoke-Ui -Verb 'focus' -Selector 'SearchBox' -ProcessId $Client.Id
    Invoke-WinApp -Arguments @('ui', 'send-keys', 'tab', '-a', [string]$Client.Id, '--via', 'send-input')
    Invoke-WinApp -Arguments @('ui', 'wait-for', 'StatusFilter', '-a', [string]$Client.Id,
        '--property', 'HasKeyboardFocus', '--value', 'True', '-t', '3000')
}

function Send-BorrowWithEnter {
    param([System.Diagnostics.Process]$Client, [string]$Note)
    Select-Equipment -Client $Client
    Invoke-Ui -Verb 'set-value' -Selector 'OperationNote' -Value $Note -ProcessId $Client.Id
    Invoke-Ui -Verb 'focus' -Selector 'OperationNote' -ProcessId $Client.Id
    Invoke-WinApp -Arguments @('ui', 'send-keys', 'enter', '-a', [string]$Client.Id, '--via', 'send-input')
}

function Send-ReturnWithEnter {
    param([System.Diagnostics.Process]$Client)
    Invoke-Ui -Verb 'focus' -Selector 'OperationNote' -ProcessId $Client.Id
    Invoke-WinApp -Arguments @('ui', 'send-keys', 'enter', '-a', [string]$Client.Id, '--via', 'send-input')
}

function Assert-WindowLayouts {
    param([System.Diagnostics.Process]$Client, [string]$RunDirectory)
    $Client.Refresh()
    $handle = $Client.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'WPF main window handle was unavailable.' }
    if (-not [DemoWindowApi]::MoveWindow($handle, 0, 0, 820, 520, $true)) { throw 'Could not resize the WPF window.' }
    Start-Sleep -Milliseconds 400
    Assert-VisibleInWindow 'EquipmentList' $Client
    Assert-VisibleInWindow 'OperationNote' $Client
    Assert-VisibleInWindow 'BorrowButton' $Client
    Save-Screenshot $Client (Join-Path $RunDirectory 'screenshots\03-compact.png')

    [DemoWindowApi]::ShowWindow($handle, 3) | Out-Null
    Start-Sleep -Milliseconds 400
    Assert-VisibleInWindow 'EquipmentList' $Client
    Assert-VisibleInWindow 'OperationNote' $Client
    Assert-VisibleInWindow 'BorrowButton' $Client
    Save-Screenshot $Client (Join-Path $RunDirectory 'screenshots\04-maximized.png')
}

function Get-OperationBodyHash {
    param([string]$BodyJson)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($BodyJson)
    $digest = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($digest).ToLowerInvariant()
}

function Wait-ForOperationLog {
    param([string]$LogDirectory, [string]$OperationId, [int]$MinimumOccurrences = 2)
    $stdout = Join-Path $LogDirectory 'api.stdout.log'
    $stderr = Join-Path $LogDirectory 'api.stderr.log'
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $logs = ''
        foreach ($path in @($stdout, $stderr)) {
            if (Test-Path $path) { $logs += Get-Content $path -Raw }
        }
        $count = [regex]::Matches($logs, [regex]::Escape($OperationId)).Count
        if ($count -ge $MinimumOccurrences) { return $count }
        Start-Sleep -Milliseconds 250
    }
    throw "API logs did not show $MinimumOccurrences attempts for operation $OperationId."
}

function New-ScenarioRun {
    param([string]$Name)
    $directory = Join-Path $outputRoot ("runs\{0}-{1}" -f $Name, [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $directory | Out-Null
    return [System.IO.Path]::GetFullPath($directory)
}

function Write-ScenarioResult {
    param([string]$Directory, [string]$Name, [hashtable]$Evidence)
    $document = [ordered]@{ scenario = $Name; result = 'passed'; verifiedAtUtc = [DateTime]::UtcNow.ToString('O'); evidence = $Evidence }
    $document | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $Directory 'result.json')
    Write-Host "[$Name] passed — $Directory"
}

function Invoke-Basic {
    $run = New-ScenarioRun 'Basic'
    $serverData = Join-Path $run 'server'
    $clientData = Join-Path $run 'client-A'
    $api = $null; $client = $null; $recorder = $null
    try {
        $api = Start-Api $serverData $false (Join-Path $run 'logs')
        $client = Start-Client $clientData 'A'
        $recorder = Start-Recording $client.Id (Join-Path $run 'videos\basic.mp4')
        Test-TabOrder $client
        Test-EscapeCancelsDraft $client
        Send-BorrowWithEnter $client 'basic demo loan'
        Wait-OperationNotice $client '대여가 완료되었습니다.'
        Start-Sleep -Seconds 2
        $borrowed = Assert-OperationCounts 1 0 '대여 중'
        Save-Screenshot $client (Join-Path $run 'screenshots\01-borrowed.png')
        Send-ReturnWithEnter $client
        Wait-OperationNotice $client '반납이 완료되었습니다.'
        Start-Sleep -Seconds 2
        $returned = Assert-OperationCounts 1 1 '사용 가능'
        Save-Screenshot $client (Join-Path $run 'screenshots\02-returned.png')
        Assert-WindowLayouts $client $run
        Complete-Recording $recorder (Join-Path $run 'videos\basic.mp4'); $recorder = $null
        Write-ScenarioResult $run 'Basic' @{ borrowed = $borrowed; returned = $returned }
    } finally {
        if ($recorder) { Stop-OwnedProcess $recorder }
        Stop-OwnedProcess $client
        Stop-OwnedProcess $api
    }
}

function Invoke-ConcurrentLoan {
    $run = New-ScenarioRun 'ConcurrentLoan'
    $serverData = Join-Path $run 'server'
    $clientAData = Join-Path $run 'client-A'
    $clientBData = Join-Path $run 'client-B'
    $api = $null; $clientA = $null; $clientB = $null; $recorder = $null
    try {
        $api = Start-Api $serverData $false (Join-Path $run 'logs')
        $clientA = Start-Client $clientAData 'A'
        $clientB = Start-Client $clientBData 'B'
        $recorder = Start-Recording $clientB.Id (Join-Path $run 'videos\concurrent-conflict.mp4')
        Begin-Borrow $clientA 'client A request'
        Wait-OperationNotice $clientA '대여가 완료되었습니다.'
        Start-Sleep -Seconds 2
        Begin-Borrow $clientB 'client B stale request'
        Wait-OperationNotice $clientB '장비 정보가 변경되었습니다.'
        $evidence = Assert-OperationCounts 1 0 '대여 중'
        Save-Screenshot $clientB (Join-Path $run 'screenshots\conflict.png')
        Complete-Recording $recorder (Join-Path $run 'videos\concurrent-conflict.mp4'); $recorder = $null
        Write-ScenarioResult $run 'ConcurrentLoan' @{ result = $evidence; losingClientNotice = 'VersionConflict' }
    } finally {
        if ($recorder) { Stop-OwnedProcess $recorder }
        Stop-OwnedProcess $clientB
        Stop-OwnedProcess $clientA
        Stop-OwnedProcess $api
    }
}

function Invoke-LostResponse {
    $run = New-ScenarioRun 'LostResponse'
    $serverData = Join-Path $run 'server'
    $clientData = Join-Path $run 'client-A'
    $api = $null; $client = $null; $recorder = $null
    try {
        $api = Start-Api $serverData $true (Join-Path $run 'logs')
        $client = Start-Client $clientData 'A'
        $recorder = Start-Recording $client.Id (Join-Path $run 'videos\lost-response-retry.mp4')
        Begin-Borrow $client 'lost response demo'
        Invoke-WinApp -Arguments @('ui', 'wait-for', 'RetryButton', '-a', [string]$client.Id, '-t', '30000')
        Invoke-WinApp -Arguments @('ui', 'wait-for', 'OperationStatus', '-a', [string]$client.Id, '--value', '요청 결과를 확인하지 못했습니다. 같은 요청으로 다시 시도할 수 있습니다.', '-t', '5000')
        $pendingPath = Join-Path $clientData 'pending-operation.json'
        $operation = Get-Content $pendingPath -Raw | ConvertFrom-Json
        $bodyHash = Get-OperationBodyHash $operation.bodyJson
        $beforeRetry = Assert-OperationCounts 1 0 '대여 중'
        Save-Screenshot $client (Join-Path $run 'screenshots\01-unknown-result.png')
        Start-Sleep -Seconds 2
        Invoke-Ui -Verb 'invoke' -Selector 'RetryButton' -ProcessId $client.Id
        Wait-OperationNotice $client '대여가 완료되었습니다.'
        if (Test-Path $pendingPath) { throw 'The resolved client operation was not removed.' }
        $afterRetry = Assert-OperationCounts 1 0 '대여 중'
        if ($beforeRetry.historyOperationIds.Count -ne 1 -or $beforeRetry.historyOperationIds[0] -ne $operation.operationId) {
            throw 'The pending operation key does not match the single committed history event.'
        }
        Save-Screenshot $client (Join-Path $run 'screenshots\02-retried.png')
        Complete-Recording $recorder (Join-Path $run 'videos\lost-response-retry.mp4'); $recorder = $null
        Stop-OwnedProcess $api; $api = $null
        $loggedAttempts = Wait-ForOperationLog (Join-Path $run 'logs') $operation.operationId
        Write-ScenarioResult $run 'LostResponse' @{ operationId = $operation.operationId; requestBodySha256 = $bodyHash; loggedAttempts = $loggedAttempts; beforeRetry = $beforeRetry; afterRetry = $afterRetry }
    } finally {
        if ($recorder) { Stop-OwnedProcess $recorder }
        Stop-OwnedProcess $client
        Stop-OwnedProcess $api
    }
}

function Invoke-ApiDown {
    $run = New-ScenarioRun 'ApiDown'
    $serverData = Join-Path $run 'server'
    $clientData = Join-Path $run 'client-A'
    $api = $null; $client = $null; $recorder = $null
    try {
        $api = Start-Api $serverData $false (Join-Path $run 'logs')
        $client = Start-Client $clientData 'A'
        $recorder = Start-Recording $client.Id (Join-Path $run 'videos\api-down-retry.mp4')
        Stop-OwnedProcess $api; $api = $null
        Begin-Borrow $client 'API unavailable demo'
        Invoke-WinApp -Arguments @('ui', 'wait-for', 'RetryButton', '-a', [string]$client.Id, '-t', '30000')
        $pendingPath = Join-Path $clientData 'pending-operation.json'
        $operation = Get-Content $pendingPath -Raw | ConvertFrom-Json
        $bodyHash = Get-OperationBodyHash $operation.bodyJson
        Save-Screenshot $client (Join-Path $run 'screenshots\01-api-down.png')
        $api = Start-Api $serverData $false (Join-Path $run 'logs-restarted')
        Start-Sleep -Seconds 2
        Invoke-Ui -Verb 'invoke' -Selector 'RetryButton' -ProcessId $client.Id
        Wait-OperationNotice $client '대여가 완료되었습니다.'
        if (Test-Path $pendingPath) { throw 'The resolved client operation was not removed.' }
        $evidence = Assert-OperationCounts 1 0 '대여 중'
        $committed = @(Get-EquipmentHistory | Where-Object kind -eq 'Borrowed')
        $originalCommand = $operation.bodyJson | ConvertFrom-Json
        if ($committed.Count -ne 1 -or $committed[0].operationId -ne $operation.operationId -or
            $committed[0].note -ne $originalCommand.note) {
            throw 'The reconnected loan does not match the saved operation key and original note.'
        }
        Save-Screenshot $client (Join-Path $run 'screenshots\02-reconnected.png')
        Complete-Recording $recorder (Join-Path $run 'videos\api-down-retry.mp4'); $recorder = $null
        Write-ScenarioResult $run 'ApiDown' @{ operationId = $operation.operationId; requestBodySha256 = $bodyHash; result = $evidence }
    } finally {
        if ($recorder) { Stop-OwnedProcess $recorder }
        Stop-OwnedProcess $client
        Stop-OwnedProcess $api
    }
}

function Invoke-RestartRecovery {
    $run = New-ScenarioRun 'RestartRecovery'
    $serverData = Join-Path $run 'server'
    $clientData = Join-Path $run 'client-A'
    $api = $null; $client = $null; $recorder = $null; $recoveryRecorder = $null
    try {
        $api = Start-Api $serverData $true (Join-Path $run 'logs')
        $client = Start-Client $clientData 'A'
        $recorder = Start-Recording $client.Id (Join-Path $run 'videos\01-before-restart.mp4')
        Begin-Borrow $client 'restart recovery demo'
        Invoke-WinApp -Arguments @('ui', 'wait-for', 'RetryButton', '-a', [string]$client.Id, '-t', '30000')
        $pendingPath = Join-Path $clientData 'pending-operation.json'
        $operation = Get-Content $pendingPath -Raw | ConvertFrom-Json
        $bodyHash = Get-OperationBodyHash $operation.bodyJson
        $committedBeforeRestart = Assert-OperationCounts 1 0 '대여 중'
        Save-Screenshot $client (Join-Path $run 'screenshots\01-before-restart.png')
        Complete-Recording $recorder (Join-Path $run 'videos\01-before-restart.mp4'); $recorder = $null
        Stop-OwnedProcess $client; $client = $null

        $client = Start-ClientProcess $clientData 'A'
        $recoveryRecorder = Start-Recording $client.Id (Join-Path $run 'videos\02-after-restart.mp4')
        Wait-ForWindow $client
        Wait-OperationNotice $client '대여가 완료되었습니다.'
        if (Test-Path $pendingPath) { throw 'Startup recovery did not remove the confirmed operation.' }
        $recovered = Assert-OperationCounts 1 0 '대여 중'
        if ($committedBeforeRestart.historyOperationIds.Count -ne 1 -or
            $committedBeforeRestart.historyOperationIds[0] -ne $operation.operationId -or
            $recovered.historyOperationIds[0] -ne $operation.operationId) {
            throw 'Restart recovery did not replay the same operation identity.'
        }
        Save-Screenshot $client (Join-Path $run 'screenshots\02-after-restart.png')
        Invoke-Ui -Verb 'set-value' -Selector 'SearchBox' -Value 'EQ-1001' -ProcessId $client.Id
        Start-Sleep -Seconds 2
        Invoke-Ui -Verb 'set-value' -Selector 'SearchBox' -Value '' -ProcessId $client.Id
        Invoke-WinApp -Arguments @('ui', 'wait-for', 'Equipment1002', '-a', [string]$client.Id, '-t', '5000')
        Complete-Recording $recoveryRecorder (Join-Path $run 'videos\02-after-restart.mp4'); $recoveryRecorder = $null
        Stop-OwnedProcess $api; $api = $null
        $loggedAttempts = Wait-ForOperationLog (Join-Path $run 'logs') $operation.operationId
        Write-ScenarioResult $run 'RestartRecovery' @{ operationId = $operation.operationId; requestBodySha256 = $bodyHash; loggedAttempts = $loggedAttempts; beforeRestart = $committedBeforeRestart; afterRestart = $recovered }
    } finally {
        if ($recorder) { Stop-OwnedProcess $recorder }
        if ($recoveryRecorder) { Stop-OwnedProcess $recoveryRecorder }
        Stop-OwnedProcess $client
        Stop-OwnedProcess $api
    }
}

if (-not (Test-Path $apiExecutable)) { throw "API executable was not found: $apiExecutable" }
if (-not (Test-Path $clientExecutable)) { throw "WPF client was not found: $clientExecutable" }
$version = (& winapp --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '0\.7\.0') { throw "WinAppCLI 0.7.0 is required; found '$version'." }

$previousEnvironment = @{}
foreach ($name in @('WPFDEMO_API_URL', 'WPFDEMO_SERVER_DATA_DIR', 'WPFDEMO_DEMO_DROP_NEXT_BORROW_RESPONSE',
        'WPFDEMO_CLIENT_DATA_DIR', 'WPFDEMO_CLIENT_LABEL', 'WINAPP_UI_WORKFLOW_ID')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $selectedScenarios = if ($Scenario -eq 'All') { @('Basic', 'ConcurrentLoan', 'LostResponse', 'ApiDown', 'RestartRecovery') } else { @($Scenario) }
    foreach ($selected in $selectedScenarios) {
        switch ($selected) {
            'Basic' { Invoke-Basic }
            'ConcurrentLoan' { Invoke-ConcurrentLoan }
            'LostResponse' { Invoke-LostResponse }
            'ApiDown' { Invoke-ApiDown }
            'RestartRecovery' { Invoke-RestartRecovery }
        }
    }
} finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}
