# FlyScreen.Relay（玄鸟自托管信令 + 中继）

独立服务，对标 Tailscale 的「协调/发现 + DERP 中继」：

| 端口 | 默认 | 用途 |
|------|------|------|
| **边缘口** | TCP `8443` | 设备注册、房间发现、信令、WebSocket 中继；HTTP `/v1/*` |
| **管理口** | TCP `9080` | 管理网页 + REST `/api/*` |
| **Peer TCP** | TCP `15155` | 兼容连接配置默认端口：可达性测试 + ProbeOnly 握手 |
| STUN | UDP `8443` | 客户端收集 srflx 候选，优先 P2P |
| **发现桥接** | UDP `15156` | 兼容现有客户端 `DiscoveryService`，应答全部在线注册设备 |

> 本目录为独立服务。下文示例用 `ws://` / `http://`；生产请用 TLS（`wss://` / `https://`）。管理页在管理口直接打开，房间号取第一台注册电脑的名称。注册记录默认写入内嵌 SQLite（`data/relay.db`）。

---

## 快速启动

```bash
cd src/FlyScreen.Relay
dotnet run
# 或 Docker
docker compose up -d --build
```

- 管理页：http://服务器:9080/
- 边缘信息：http://服务器:8443/v1/info
- 冒烟脚本：`tools/smoke-test.ps1`（需服务已启动）

---

## Docker 打包镜像

工程内脚本会构建镜像并打上 `latest` + 版本标签（默认取 git describe，否则用时间戳）。

```powershell
cd src/FlyScreen.Relay

# 仅构建（国内建议加 -Mirror，走 DaoCloud 代理 mcr）
.\tools\pack-docker.ps1 -Mirror

# 指定版本并导出 tar（便于拷到服务器 docker load）
.\tools\pack-docker.ps1 -Mirror -Tag 1.0.0 -Save

# 推送到私有仓库
.\tools\pack-docker.ps1 -Tag 1.0.0 -Registry registry.example.com/flyscreen -Push

# 从 tar 加载
.\tools\pack-docker.ps1 -LoadPath .\out\flyscreen-relay-1.0.0.tar
```

Linux / macOS：

```bash
chmod +x tools/pack-docker.sh
./tools/pack-docker.sh --mirror --tag 1.0.0 --save
```

导出文件默认在 `out/flyscreen-relay-<tag>.tar`。服务器上：

```bash
docker load -i flyscreen-relay-1.0.0.tar
docker compose up -d
# 或 docker run … 见脚本输出提示
```

下文示例默认服务器地址为 `relay.example.com`，请换成你的 IP 或域名。

---

## 1. 如何注册

设备必须先连边缘 WebSocket，发送 `hello`，进入某个房间后才会出现在发现列表里。

### 1.1 步骤

```text
客户端                              中继边缘口 :8443
   |                                      |
   |-------- WS 升级 /ws ---------------->|
   |<------- welcome（含 STUN / 连接建议）-|
   |-------- hello（身份 + 候选）-------->|
   |<------- registered + devices 列表 ---|
   |<------- 同房间其它设备 device.updated|
   |-------- ping / candidates（可选）---->|
```

1. 连接 `ws://relay.example.com:8443/ws`（TLS 环境用 `wss://`）。
2. 收到 `welcome` 后立刻发 `hello`（未注册前其它消息会被拒绝）。
3. 收到 `registered` 即注册成功；随后会收到同房间 `devices` 列表。
4. 保持连接；断线即离线。可用 `ping` 保活，网卡变化时发 `candidates` 更新地址。

### 1.2 `hello` 示例

```json
{
  "type": "hello",
  "deviceId": "a1b2c3d4e5f6789012345678abcdef01",
  "name": "家里台式机",
  "roomKey": "与玄鸟安全密钥相同的字符串",
  "publicKey": "可选-设备公钥指纹",
  "platform": "windows",
  "tcpPort": 15155,
  "candidates": [
    { "kind": "host", "address": "192.168.1.20:15155", "priority": 100 },
    { "kind": "ipv6", "address": "[2001:db8::20]:15155", "priority": 90 }
  ]
}
```

| 字段 | 说明 |
|------|------|
| `deviceId` | 本机稳定 Id（建议 GUID 去横线）。空则服务端分配。同 Id 重连会踢掉旧连接。 |
| `name` | 显示名，会出现在 UDP 发现的 `MachineName` 里。 |
| `roomKey` | **房间密钥**。服务端存 `SHA256(roomKey)` 前 16 位 hex 作为 `roomId`；同一密钥才能互见、互发信令。建议与客户端「安全密钥」一致。 |
| `candidates` | 本机可达地址，供对端做 P2P。`kind`：`host` / `ipv6` / `srflx`（STUN 后补）。 |

### 1.3 服务端应答示例

```json
{ "type": "welcome", "serverName": "flyscreen-relay", "stun": { "host": "relay.example.com", "port": 8443 },
  "connectAdvice": { "mode": "prefer-p2p", "p2PTimeoutMs": 5000, "tryOrder": ["ipv6", "host", "srflx", "relay"] } }
```

```json
{ "type": "registered", "ok": true, "deviceId": "a1b2c3d4e5f6789012345678abcdef01", "roomId": "2a97516c354b6884" }
```

```json
{ "type": "devices", "devices": [ { "id": "…", "name": "公司笔记本", "online": true, "tcpPort": 15155, "candidates": [ … ] } ] }
```

### 1.4 PowerShell 最小注册示例

```powershell
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]"ws://relay.example.com:8443/ws", [Threading.CancellationToken]::None).Wait()

# 先读 welcome（略），再发 hello
$hello = @{
  type = "hello"
  deviceId = [guid]::NewGuid().ToString("N")
  name = $env:COMPUTERNAME
  roomKey = "demo-room"
  platform = "windows"
  tcpPort = 15155
  candidates = @(@{ kind = "host"; address = "10.10.1.20:15155"; priority = 100 })
} | ConvertTo-Json -Compress -Depth 5

$bytes = [Text.Encoding]::UTF8.GetBytes($hello)
$ws.SendAsync([ArraySegment[byte]]$bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true,
  [Threading.CancellationToken]::None).Wait()

# 保持 $ws 不 Dispose，设备才会显示为在线
```

### 1.5 HTTP 核对是否注册成功

```bash
# 按房间密钥列设备
curl "http://relay.example.com:8443/v1/devices?roomKey=demo-room"

# 列全部在线设备（调试用）
curl "http://relay.example.com:8443/v1/discover"
```

管理页 http://relay.example.com:9080/ ：左侧「首页」是看板，「房间」列出已注册房间及其设备。房间号是该房间第一台注册电脑的名称。

---

## 2. 如何广播 / 发现设备

注册只解决「谁在中继上」；让其它电脑的**设备管理**扫到它们，靠发现口。

### 2.1 两种发现路径

| 路径 | 端口 | 谁触发 | 结果 |
|------|------|--------|------|
| **UDP 发现桥接** | UDP `15156` | 客户端广播 / 对服务器 IP 单播 / 服务端周期广播 | 每台在线设备一条 `DiscoveryPacket` |
| **HTTP** | TCP `8443` | `GET /v1/devices?roomKey=` 或 `/v1/discover` | JSON 设备列表 |
| **TCP 探测** | TCP `15155` | 连接配置「测试」/ ProbeOnly | 只回报**服务名**（如 `flyscreen-relay`），不代替多设备列表 |

多设备进设备管理，主要靠 **UDP 15156**。

### 2.2 服务端行为

```text
客户端（或局域网广播）                    中继 UDP :15156
   |  DiscoveryPacket / 任意探测包              |
   |------------------------------------------>|
   |  对每台在线设备各回一条：                   |
   |  {"MachineName","Ip","TcpPort"}  <--------|
   |  …（N 台则 N 条）                          |
```

- 收到合法探测包 → **单播回探测方**，把当前所有在线注册设备宣告一遍。
- `DiscoveryBroadcastAnnounce=true`（默认）时，约每 2 秒向 IPv4 广播宣告一次（同局域网 / VPN 无需单播也能扫到）。
- 客户端 Prune 约 8 秒，故需设备保持 WS 在线，否则列表会掉。

宣告包格式（与 Core `DiscoveryService` 一致，PascalCase）：

```json
{ "MachineName": "家里台式机", "Ip": "192.168.1.20", "TcpPort": 15155 }
```

`Ip` / `TcpPort` 优先取设备 `hello` 里的 `candidates`，否则用注册时看到的远端地址。

### 2.3 现有玄鸟客户端怎么配（不改客户端代码）

1. 两端先按 §1 连上中继并 `hello`（后续客户端版本会内置；现在可用脚本模拟）。
2. 在「网络 → 连接配置」添加 **远程**，地址填 `relay.example.com` 或 `relay.example.com:15155`。
3. 点「测试」→ 走 TCP `15155` ProbeOnly，通过则入口可达。
4. 「设备管理 → 刷新发现」→ 客户端向 `服务器:15156` **UDP 单播** → 中继回多条宣告 → 设备出现在列表里。

> 若填 `wss://…`，当前客户端会当成「中转」类型，**还不会走这套 UDP 探测**。要用现有刷新发现，请先用 **IP/主机名** 作为远程行。

### 2.4 手动 UDP 探测示例（PowerShell）

```powershell
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Client.ReceiveTimeout = 2000
$probe = [Text.Encoding]::UTF8.GetBytes('{"MachineName":"Probe","Ip":"10.10.1.1","TcpPort":15155}')
[void]$udp.Send($probe, $probe.Length, "relay.example.com", 15156)

$from = New-Object System.Net.IPEndPoint([Net.IPAddress]::Any, 0)
while ($true) {
  try {
    $buf = $udp.Receive([ref]$from)
    [Text.Encoding]::UTF8.GetString($buf)   # 每台在线设备一行 JSON
  } catch { break }
}
$udp.Close()
```

### 2.5 STUN（为后面 P2P 收集公网映射）

`welcome.stun` 给出 STUN 地址（默认 UDP 与边缘同号 `8443`）。客户端发 RFC 5389 Binding Request，用返回的 XOR-MAPPED-ADDRESS 作为 `srflx` 候选，再通过 `candidates` 或 `signal` 发给对端。

---

## 3. 两台已注册设备：先 P2P，失败再中继

前提：设备 A、B 都已 `hello` 注册，且 **`roomKey` 相同**（同一房间）。

服务端建议顺序（`connectAdvice.tryOrder`）：

```text
ipv6 → host → srflx → relay
```

P2P 建议超时约 **5 秒**（`p2PTimeoutMs`），超时或全部候选失败再走中继。

### 3.1 总流程

```text
        A                                           B
        |                                           |
        |  signal: 交换 candidates / offer-answer     |
        |<-------- 经中继信令转发（同房间）----------->|
        |                                           |
        |==== 尝试 P2P（IPv6 / 局域网 / STUN打洞）====|
        |         成功 → 直连传业务数据，结束          |
        |         失败（约 5s）↓                      |
        |                                           |
        |  relay.open (同一 sessionId)                |
        |-----------→ 中继 ←-------------------------|
        |<------ relay.ready ----------------------->|
        |                                           |
        |==== 二进制帧经中继互转（端到端密文）==========|
```

### 3.2 步骤 A：交换候选（信令，不经业务 TCP）

双方把候选发给对端（中继只转发，不改内容）：

```json
{
  "type": "signal",
  "to": "对端deviceId",
  "msgType": "candidate",
  "payload": {
    "candidates": [
      { "kind": "ipv6", "address": "[2001:db8::20]:15155", "priority": 100 },
      { "kind": "host", "address": "192.168.1.20:15155", "priority": 80 },
      { "kind": "srflx", "address": "203.0.113.10:54321", "priority": 60 }
    ]
  }
}
```

对端收到的服务端转发形如：

```json
{ "type": "signal", "from": "A的deviceId", "to": "B的deviceId", "msgType": "candidate", "payload": { … } }
```

可选：用 `msgType: "offer"` / `"answer"` 协商会话密钥或连接参数；`connect-result` 通知对端 P2P 成败。

### 3.3 步骤 B：客户端本地尝试 P2P

按优先级依次尝试（**这是客户端逻辑，中继不代连**）：

1. **IPv6**：双方都有全局 IPv6 且防火墙允许 → 直连对端 `tcpPort`（默认 15155）。
2. **host**：同一局域网 / VPN 上的私网地址。
3. **srflx**：用 STUN 得到的公网映射做 UDP/TCP 打洞（视客户端实现）。

任一路双向连通即视为 P2P 成功，后续键鼠/投影/文件走直连，**不再占用中继带宽**。

失败判定示例：

```json
{
  "type": "signal",
  "to": "对端deviceId",
  "msgType": "connect-result",
  "payload": { "ok": false, "mode": "p2p", "reason": "timeout", "triedMs": 5000 }
}
```

### 3.4 步骤 C：降级中继

双方使用**同一个** `sessionId`（32 位 hex 的 GUID，无横线）：

```json
{
  "type": "relay.open",
  "peerId": "对端deviceId",
  "sessionId": "0123456789abcdef0123456789abcdef"
}
```

时序：

1. **先到的一端**：服务端回复等待（`relay-waiting` / 信令 `relay-request` 通知对端），并占用该 `sessionId`。
2. **后到的一端**：两边都 `relay.open` 成功后，服务端向双方推送：

```json
{ "type": "relay.ready", "ok": true, "sessionId": "0123456789abcdef0123456789abcdef", "peerId": "…" }
```

3. 之后在**同一条 WebSocket** 上发送**二进制帧**（中继原样转给对端，不解密）：

```text
偏移  长度   含义
0     1     魔数 0x01
1     16    sessionId 的 GUID 字节（与上面 hex 对应）
17    N     业务密文（端到端加密后的载荷）
```

4. 结束会话：

```json
{ "type": "relay.close", "sessionId": "0123456789abcdef0123456789abcdef" }
```

对端会收到 `relay.peer-left`。空闲超时由 `RelayIdleTimeoutSeconds`（默认 600）清理。

### 3.5 完整对话示例（摘要）

```text
# --- 注册（A、B 各做一次）---
A→S  hello { deviceId:A, name:"Home", roomKey:"family", candidates:[…] }
S→A  registered / devices
B→S  hello { deviceId:B, name:"Office", roomKey:"family", candidates:[…] }
S→B  registered / devices
S→A  device.updated { B }

# --- P2P ---
A→S  signal { to:B, msgType:"candidate", payload:{…} }
S→B  signal { from:A, … }
B→S  signal { to:A, msgType:"candidate", payload:{…} }
S→A  signal { from:B, … }
A⇄B  直连尝试 ipv6/host/srflx …… 失败

# --- 中继 ---
A→S  relay.open { peerId:B, sessionId:S1 }
S→B  signal { msgType:"relay-request", sessionId:S1 }
B→S  relay.open { peerId:A, sessionId:S1 }
S→A  relay.ready { sessionId:S1, peerId:B }
S→B  relay.ready { sessionId:S1, peerId:A }
A→S  二进制 0x01|S1|密文
S→B  原样转发
B→S  二进制 0x01|S1|密文
S→A  原样转发
```

### 3.6 房间与安全注意

- 不同 `roomKey` **不能**互发 `signal` / `relay.open`（服务端返回 `room_mismatch` / 对端不在线）。
- 中继只转发密文；会话密钥应由 A、B 自行协商（Noise / ECDH 等），**不要**把明文业务丢进中继帧。
- `sessionId` 必须是合法 GUID 的 `N` 格式，以便二进制帧 16 字节与文本 `sessionId` 一致。

---

## 与现有客户端「刷新发现」对照

| 客户端动作 | 打到本服务 | 期望结果 |
|------------|------------|----------|
| 连接配置「测试」`IP` / `IP:15155` | TCP `15155` ProbeOnly | 可达 + 服务名 |
| 设备管理「刷新发现」 | UDP `IP:15156` 单播 | N 条在线设备宣告 |
| 同网广播 | UDP `15156` | 周期/应答宣告 |
| 连接配置「服务」`ws://IP:8443` 或 `IP:8443` | WS `/ws` + hello | 注册进房间；默认可省略写成 `IP`（客户端默认边缘口 8443） |

---

## 管理 API

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/health` | 健康与计数 |
| GET | `/api/dashboard` | 首页看板 |
| GET | `/api/rooms` | 已注册房间（房间号为第一台电脑名称） |
| GET | `/api/rooms/{roomId}` | 房间内设备 |
| DELETE | `/api/devices/{id}` | 踢出在线设备（注册记录保留） |
| POST | `/api/sessions/{id}/close` | 关闭会话 |

管理口不再要求令牌。请只在内网或反向代理后开放 `9080`。

---

## 配置（`appsettings.json` / 环境变量 `Relay__*`）

| 项 | 默认 | 含义 |
|----|------|------|
| EdgeListen | `0.0.0.0:8443` | 边缘 TCP |
| AdminListen | `0.0.0.0:9080` | 管理 TCP |
| PeerTcpPort | `15155` | 兼容客户端默认 TCP；`0` 关闭 |
| StunUdpPort | `8443` | STUN；`0` 关闭 |
| DiscoveryUdpPort | `15156` | 兼容客户端发现；`0` 关闭 |
| DiscoveryBroadcastAnnounce | `true` | 周期广播宣告 |
| Database:UseEmbedded | `true` | 使用内嵌 SQLite。设为 `false` 时必须同时给出外部库，否则启动失败 |
| Database:EmbeddedPath | `data/relay.db` | 内嵌库文件，相对内容根目录 |
| Database:Provider | 空 | 外部库类型：`mysql` 或 `mariadb`。仅 `UseEmbedded=false` 时生效 |
| Database:ConnectionString | 空 | 外部库连接串。仅 `UseEmbedded=false` 时生效 |
| PublicEdgeUrl | 空 | 对外公布的 wss 根地址 |
| MaxDevicesPerRoom | `64` | 单房间设备上限 |
| MaxRelaySessions | `128` | 同时中继会话上限 |
| RelayIdleTimeoutSeconds | `600` | 中继空闲回收 |

防火墙需放行：TCP `8443`、`9080`、`15155`；UDP `8443`、`15156`。
