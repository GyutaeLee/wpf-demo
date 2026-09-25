$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public struct NativeRect {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class WindowApi {
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
}
'@

function Run-WinApp {
    & winapp @args
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($args -join ' ')" }
}

function Assert-VisibleInWindow {
    param([string]$Selector, [string]$Target, [IntPtr]$Handle)
    $result = & winapp ui get-property $Selector -a $Target --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect $Selector" }
    $element = $result.element
    $window = New-Object NativeRect
    if (-not [WindowApi]::GetWindowRect($Handle, [ref]$window)) { throw 'Cannot read window bounds.' }
    if ($element.isOffscreen -or $element.width -le 0 -or $element.height -le 0 -or
        $element.x -lt $window.Left -or $element.y -lt $window.Top -or
        ($element.x + $element.width) -gt $window.Right -or
        ($element.y + $element.height) -gt $window.Bottom) {
        throw "$Selector is outside the window at this size."
    }
}

function Start-Api {
    $process = Start-Process -FilePath "$PWD/artifacts/api/WpfDemo.Api.exe" -PassThru -WindowStyle Hidden
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $null = Invoke-RestMethod 'http://127.0.0.1:5187/api/work-items' -TimeoutSec 2
            return $process
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw 'Local API did not start.'
}

New-Item -ItemType Directory -Force artifacts/screenshots | Out-Null
$api = $null
$client = $null
try {
    $api = Start-Api
    $client = Start-Process -FilePath "$PWD/artifacts/client/WpfDemo.exe" -PassThru
    $target = "$($client.Id)"

    Run-WinApp @('ui', 'wait-for', 'WorkItem1001', '-a', $target, '-t', '30000')
    $status = & winapp ui status -a $target --json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read Windows DPI status.' }
    $status | Set-Content artifacts/windows-ui-status.json
    Run-WinApp @('ui', 'focus', 'SearchBox', '-a', $target)
    Run-WinApp @('ui', 'send-keys', 'tab', '-a', $target, '--via', 'send-input')
    Run-WinApp @('ui', 'wait-for', 'StatusFilter', '-a', $target, '--property', 'HasKeyboardFocus', '--value', 'True', '-t', '3000')
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/01-list.png')
    Run-WinApp @('ui', 'click', 'WorkItem1001', '-a', $target)
    Run-WinApp @('ui', 'wait-for', 'EditNote', '-a', $target, '-t', '5000')
    Run-WinApp @('ui', 'set-value', 'EditNote', '가상 메모 수정 완료', '-a', $target)
    Run-WinApp @('ui', 'focus', 'SaveButton', '-a', $target)
    Run-WinApp @('ui', 'send-keys', 'enter', '-a', $target, '--via', 'send-input')
    Run-WinApp @('ui', 'wait-for', 'SaveNotice', '-a', $target, '--value', '저장되었습니다.', '-t', '10000')
    $saved = Invoke-RestMethod 'http://127.0.0.1:5187/api/work-items'
    if (($saved | Where-Object id -eq 1001).note -ne '가상 메모 수정 완료') { throw 'API did not persist the edited memo.' }
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/02-saved.png')

    Stop-Process -Id $api.Id -Force
    $api = $null
    Run-WinApp @('ui', 'set-value', 'EditNote', '중단 후 다시 저장', '-a', $target)
    Run-WinApp @('ui', 'invoke', 'SaveButton', '-a', $target)
    Run-WinApp @('ui', 'wait-for', 'SaveError', '-a', $target, '-t', '10000')
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/03-save-error.png')
    $api = Start-Api
    Run-WinApp @('ui', 'invoke', 'SaveButton', '-a', $target)
    Run-WinApp @('ui', 'wait-for', 'SaveNotice', '-a', $target, '--value', '저장되었습니다.', '-t', '10000')
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/04-retried.png')

    Stop-Process -Id $api.Id -Force
    $api = $null
    Run-WinApp @('ui', 'invoke', 'ReloadButton', '-a', $target)
    Run-WinApp @('ui', 'wait-for', 'LoadRetryButton', '-a', $target, '-t', '10000')
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/05-load-error.png')
    $api = Start-Api
    Run-WinApp @('ui', 'invoke', 'LoadRetryButton', '-a', $target)
    Run-WinApp @('ui', 'wait-for', 'WorkItem1001', '-a', $target, '-t', '10000')
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/06-load-retried.png')

    Run-WinApp @('ui', 'click', 'WorkItem1001', '-a', $target)
    $client.Refresh()
    $handle = $client.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw 'WPF window handle is unavailable.' }
    if (-not [WindowApi]::MoveWindow($handle, 0, 0, 820, 500, $true)) { throw 'Cannot resize WPF window.' }
    Start-Sleep -Milliseconds 300
    Assert-VisibleInWindow 'WorkItemList' $target $handle
    Assert-VisibleInWindow 'SaveButton' $target $handle
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/07-compact.png')
    Run-WinApp @('ui', 'focus', 'EditStatus', '-a', $target)
    Run-WinApp @('ui', 'send-keys', 'tab', '-a', $target, '--via', 'send-input')
    Run-WinApp @('ui', 'wait-for', 'EditNote', '-a', $target, '--property', 'HasKeyboardFocus', '--value', 'True', '-t', '3000')
    Assert-VisibleInWindow 'EditNote' $target $handle
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/09-compact-note.png')

    [WindowApi]::ShowWindow($handle, 3) | Out-Null
    Start-Sleep -Milliseconds 300
    Assert-VisibleInWindow 'WorkItemList' $target $handle
    Assert-VisibleInWindow 'SaveButton' $target $handle
    Run-WinApp @('ui', 'screenshot', '-a', $target, '-o', 'artifacts/screenshots/08-maximized.png')
} finally {
    if ($client -and -not $client.HasExited) { Stop-Process -Id $client.Id -Force }
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force }
}
