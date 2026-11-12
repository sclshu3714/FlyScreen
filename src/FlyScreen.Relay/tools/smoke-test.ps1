# 冒烟：注册两台假设备，检查 /v1/discover 与管理 API
$ErrorActionPreference = "Stop"
$edge = "http://127.0.0.1:8443"
$admin = "http://127.0.0.1:9080"

function Register-FakeDevice([string]$name, [string]$id) {
  $ws = New-Object System.Net.WebSockets.ClientWebSocket
  $uri = [Uri]"ws://127.0.0.1:8443/ws"
  $cts = New-Object System.Threading.CancellationTokenSource
  $ws.ConnectAsync($uri, $cts.Token).Wait()
  $hello = @{ type = "hello"; deviceId = $id; name = $name; roomKey = "demo"; platform = "test"; tcpPort = 15155; candidates = @(@{ kind = "host"; address = "10.10.1.50:15155" }) } | ConvertTo-Json -Compress
  $bytes = [Text.Encoding]::UTF8.GetBytes($hello)
  $ws.SendAsync([ArraySegment[byte]]$bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()
  Start-Sleep -Milliseconds 300
  return @{ Ws = $ws; Cts = $cts }
}

Write-Host "GET $edge/v1/info"
Invoke-RestMethod "$edge/v1/info" | ConvertTo-Json -Compress

$a = Register-FakeDevice "Smoke-A" "smokea001"
$b = Register-FakeDevice "Smoke-B" "smokeb001"
Start-Sleep -Milliseconds 400

Write-Host "GET $edge/v1/discover"
$disc = Invoke-RestMethod "$edge/v1/discover"
$disc | ConvertTo-Json -Depth 5
if ($disc.count -lt 2) { throw "expected >=2 devices" }

Write-Host "GET $admin/api/health"
Invoke-RestMethod "$admin/api/health" | ConvertTo-Json -Compress

$a.Ws.Dispose(); $b.Ws.Dispose()
Write-Host "OK"
