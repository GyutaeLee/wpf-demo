param(
    [Parameter(Mandatory = $true)] [string] $BaseAddress,
    [int] $WarmupRequests = 1,
    [int] $MeasuredRequests = 5,
    [string] $EquipmentPath = '/api/equipment',
    [string] $HistoryPath = '/api/equipment/1001/history',
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
if ($WarmupRequests -lt 0 -or $MeasuredRequests -lt 1) {
    throw 'WarmupRequests must be zero or greater, and MeasuredRequests must be at least one.'
}

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(60)

function Measure-Endpoint([string] $Name, [string] $Path) {
    $times = [System.Collections.Generic.List[double]]::new()
    $sizes = [System.Collections.Generic.List[int]]::new()
    $itemCounts = [System.Collections.Generic.List[int]]::new()
    $totalCounts = [System.Collections.Generic.List[int]]::new()
    $totalRequests = $WarmupRequests + $MeasuredRequests

    for ($index = 0; $index -lt $totalRequests; $index++) {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, ([Uri]::new([Uri]($BaseAddress.TrimEnd('/') + '/'), $Path.TrimStart('/'))))
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseContentRead).GetAwaiter().GetResult()
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $watch.Stop()
            if (-not $response.IsSuccessStatusCode) {
                throw "GET $Path returned HTTP $([int]$response.StatusCode)."
            }

            $document = [System.Text.Json.JsonDocument]::Parse([System.Text.Encoding]::UTF8.GetString($bytes))
            try {
                $root = $document.RootElement
                if ($root.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
                    $itemCount = $root.GetArrayLength()
                    $totalCount = $itemCount
                } else {
                    $items = [System.Text.Json.JsonElement]::new()
                    if ($root.ValueKind -eq [System.Text.Json.JsonValueKind]::Object -and $root.TryGetProperty('items', [ref]$items)) {
                        $itemCount = $items.GetArrayLength()
                        $totalElement = [System.Text.Json.JsonElement]::new()
                        $totalCount = if ($root.TryGetProperty('totalCount', [ref]$totalElement)) { $totalElement.GetInt32() } else { $itemCount }
                    } else {
                        $itemCount = 0
                        $totalCount = 0
                    }
                }
            } finally {
                $document.Dispose()
            }

            if ($index -ge $WarmupRequests) {
                $times.Add($watch.Elapsed.TotalMilliseconds)
                $sizes.Add($bytes.Length)
                $itemCounts.Add($itemCount)
                $totalCounts.Add($totalCount)
            }
            $response.Dispose()
        } finally {
            $request.Dispose()
        }
    }

    $orderedTimes = @($times | Sort-Object)
    $orderedSizes = @($sizes | Sort-Object)
    $middle = [Math]::Floor($orderedTimes.Count / 2)
    if ($orderedTimes.Count % 2 -eq 0) {
        $median = ($orderedTimes[$middle - 1] + $orderedTimes[$middle]) / 2
    } else {
        $median = $orderedTimes[$middle]
    }

    return [ordered]@{
        name = $Name
        path = $Path
        warmupRequests = $WarmupRequests
        measuredRequests = $MeasuredRequests
        responseItemCounts = @($itemCounts)
        responseTotalCounts = @($totalCounts)
        bodyBytes = $orderedSizes[0]
        medianMilliseconds = [Math]::Round($median, 2)
        minMilliseconds = [Math]::Round($orderedTimes[0], 2)
        maxMilliseconds = [Math]::Round($orderedTimes[-1], 2)
        samplesMilliseconds = @($times | ForEach-Object { [Math]::Round($_, 2) })
    }
}

try {
    $measurements = @(
        (Measure-Endpoint 'equipment' $EquipmentPath),
        (Measure-Endpoint 'history' $HistoryPath)
    )
    $result = [ordered]@{
        capturedAtUtc = [DateTime]::UtcNow.ToString('O')
        host = [ordered]@{
            os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            powershell = $PSVersionTable.PSVersion.ToString()
        }
        baseAddress = $BaseAddress.TrimEnd('/')
        measurements = $measurements
    }
    $json = $result | ConvertTo-Json -Depth 8
    if ($OutputPath) {
        $directory = Split-Path -Parent $OutputPath
        if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
        Set-Content -Path $OutputPath -Value $json -Encoding utf8
    }
    Write-Output $json
} finally {
    $client.Dispose()
}
