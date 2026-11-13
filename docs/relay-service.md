# 自托管中继服务说明（FlyScreen.Relay）

| 项 | 内容 |
|----|------|
| 工程 | `src/FlyScreen.Relay` |
| 性质 | 独立可部署服务；客户端按 Origin 区分本地/远程/服务 |
| 对标 | Tailscale 控制面式管理 + DERP 式边缘中继 |
| 房间 / 管理令牌 | 客户端用「安全密钥」作 `hello.roomKey`（同钥同房）；管理口 `AdminToken` 建议与安全密钥设为同一值 |
| Docker 打包 | `src/FlyScreen.Relay/tools/pack-docker.ps1`（Windows）/ `pack-docker.sh`（Linux）；详见工程 `README.md` |

## 端口

1. **管理口**（默认 `9080`）：网页 + `/api/*`
2. **边缘口**（默认 `8443`）：`/ws` 注册·信令·中继 + `/v1/*` HTTP 发现
3. **Peer TCP**（默认 `15155`）：兼容连接配置默认端口；「测试」/ ProbeOnly 握手
4. **STUN UDP**（默认与边缘同号 `8443`）
5. **发现 UDP**（默认 `15156`）：兼容现网 `DiscoveryPacket`

## UDP 多设备发现（兼容现网客户端）

客户端「刷新发现」会对连接配置中的远程 host 向 UDP `15156` 单播。本服务收到探测后，对**每个已注册在线设备**回一条：

```json
{ "MachineName": "家里台式机", "Ip": "x.x.x.x", "TcpPort": 15155, "Origin": "service" }
```

`Origin=service` 让设备管理类型显示为「服务」；局域网广播无此字段 →「本地」；连接配置远程一对一探测 →「远程」。

同网/VPN 下还可周期广播。设备须先经边缘 `/ws` `hello` 注册，否则列表为空。

## 连接建议

客户端绑定远程后应：先 P2P（IPv6 / host / STUN srflx），失败再 `relay.open`。详见工程内 `README.md`。
