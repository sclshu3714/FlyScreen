# 视频墙：源协议与编码支持矩阵

| 项 | 内容 |
|----|------|
| 性质 | 「支持市面所有格式地址」的落地口径：协议矩阵、编码矩阵、地址归一化、设备接入（ONVIF / GB28181）、不支持清单 |
| 日期 | 2026-11-13 |
| 配套 | [video-wall.md](video-wall.md)（需求/验收）· [video-wall-feasibility.md](video-wall-feasibility.md)（内核与性能） |
| 口径原则 | **凡本文未列「支持」的，一律按不支持处理**；对外表述必须用本文的分级（P0/P1/P2），禁止写「支持所有格式」 |

---

## 0. 一句话结论

「所有格式」在工程上只能拆成三层：
**① 传输协议**（怎么连上）→ **② 封装容器**（怎么拆包）→ **③ 编码**（怎么解）。
libVLC 一期能覆盖绝大多数 ①②③ 的组合，但**国标 GB28181、ONVIF 发现、DRM、H.266/VVC** 四类必须单独对待——前两类要自研适配层（P1），后两类明确不支持。

---

## 1. 传输协议矩阵

| 协议 | 典型地址 | P0 | P1/P2 | 说明与坑 |
|------|----------|----|-------|----------|
| 本地文件 | `D:\video\a.mp4`、`\\nas\share\a.mkv`、`file:///D:/a.mp4` | ✅ | — | UNC 路径需网络凭据时走系统凭据；不支持长路径 `\\?\` 之外的异常路径 |
| HTTP(S) 直链 | `https://cdn/a.mp4`、`http://host/a.flv` | ✅ | — | 支持 Range/重定向；`Content-Type` 不可靠时靠扩展名+探测 |
| HLS | `https://host/live.m3u8`、`…/index.m3u8?token=…` | ✅ | LL-HLS（P2 评估） | 支持 `EXT-X-KEY`（AES-128）；**不支持**自定义密钥服务/DRM；首帧 2–5s |
| DASH | `https://host/manifest.mpd` | ✅（基础） | 完整特性 P2 | libVLC 的 DASH 支持有限（无 DRM、部分 `SegmentBase` 异常）；异常时回退提示 |
| RTSP | `rtsp://user:pass@host:554/…` | ✅ | — | 支持 TCP/UDP 传输、Basic/Digest；`RTP over HTTP` 隧道 P1 |
| RTP / UDP 组播 | `udp://@239.1.1.1:5000`、`rtp://@…` | ✅ | — | 组播需网络支持 IGMP；单播裸 RTP 需 SDP 文件（P1） |
| RTMP / RTMPS | `rtmp://host/app/stream`、`rtmps://host:443/app/stream` | ✅ | — | 仅拉流；推流属 P2 输出合流 |
| SRT | `srt://host:9000?mode=caller&latency=200000` | ✅ | — | 依赖内核构建含 `libsrt`；`mode=listener` 需 `localMediaListen` 权限 |
| SMB / FTP / SFTP | `smb://host/share/a.mp4` | 部分 | P1 | 依赖内核构建与插件；优先建议映射为本地盘后用文件路径 |
| WebRTC / WHEP | `https://host/whep/…` | ❌ | P2 评估 | 需自建 ICE/DTLS/SRTP 栈（`SIPSorcery` 可做），成本高 |
| GB28181（SIP + RTP/PS） | 设备 ID / 平台地址（非 URL 形态） | ❌ | ✅ P1 | 见 §4；**不是**一个「地址」，而是一套会话 |
| ONVIF（发现 + 取流） | `http://host/onvif/device_service` | ❌ | ✅ P1 | 见 §5；最终仍落到 RTSP 地址 |
| 私有 SDK 协议 | 海康/大华 SDK 直连、私有 8000 端口协议 | ❌ | ❌ | 需要厂商 SDK（闭源、平台绑定），**不做**；引导用户用 RTSP/GB28181 |

---

## 2. 封装 / 容器矩阵

| 容器 | 常见扩展 | P0 | 备注 |
|------|----------|----|------|
| MP4 / MOV | `.mp4 .m4v .mov` | ✅ | 支持 `moov` 在尾部（需 seek 或预解析） |
| Matroska | `.mkv .webm` | ✅ | 多轨时默认选第一条视频 + 第一条音频 |
| MPEG-TS | `.ts .m2ts .mts` | ✅ | 直播/摄像头最常见；PS 封装另见 §4 |
| MPEG-PS | `.ps .mpg` | ✅ | GB28181 的 RTP 载荷就是 PS，复用同一 demux |
| AVI | `.avi` | ✅ | 老编码（MPEG-4 ASP、MJPEG）常见 |
| FLV | `.flv` | ✅ | RTMP 落盘文件 |
| ASF / WMV | `.wmv .asf` | ✅ | VC-1/WMV3 |
| Ogg | `.ogv .ogg` | ✅ | Theora/Vorbis（少见） |
| 3GP | `.3gp` | ✅ | 手机录像 |
| RM / RMVB | `.rm .rmvb` | ✅（软解） | RV40；无硬解 |
| 图片序列 / 单图 | `.jpg .png` | ✅（静态 1fps 刷新） | 摄像头抓拍轮询场景可当「伪视频」 |
| 加密/DRM 容器 | — | ❌ | Widevine / PlayReady / FairPlay / GB35114 加密流 |

---

## 3. 编码矩阵

### 3.1 视频

| 编码 | Profile / 位深 | P0 | 硬解（Windows） | 备注 |
|------|----------------|----|------------------|------|
| H.264 / AVC | Baseline/Main/High，8-bit | ✅ | ✅ D3D11VA | 摄像头绝对主力；High 10 走软解 |
| H.265 / HEVC | Main，8-bit | ✅ | ✅（GPU 支持时） | 8-bit 硬解普遍；**10-bit / 4:2:2** 多数消费卡不支持 → 软解或失败 |
| MPEG-2 | Main@Main | ✅ | 部分 | 老 NVR / DVD |
| MPEG-4 Part 2 | ASP | ✅ | ❌ | 老摄像头 |
| MJPEG | — | ✅ | ❌（解码轻） | 低带宽摄像头、抓拍 |
| VP8 / VP9 | — | ✅ | VP9 部分 | WebM |
| AV1 | Main，8/10-bit | ✅（软解 dav1d） | 新卡支持 | 直播课 CDN 开始出现 |
| VC-1 / WMV3 | — | ✅ | 部分 | 老文件 |
| ProRes | 422/4444 | ✅（软解） | ❌ | 影视素材，非安防 |
| RV40 | — | ✅（软解） | ❌ | RMVB |
| H.266 / VVC | — | ❌ | ❌ | 内核与硬件均未铺开，P2 评估 |
| 私有编码 | — | ❌ | ❌ | 厂商私有（如某些智能分析副码流） |

### 3.2 音频

| 编码 | P0 | 备注 |
|------|----|------|
| AAC / HE-AAC | ✅ | 直播课、MP4 |
| MP3 | ✅ | — |
| AC3 / EAC3 | ✅ | — |
| Opus / Vorbis | ✅ | WebM/WebRTC |
| FLAC | ✅ | 无损 |
| G.711 A-law / μ-law | ✅ | 摄像头/国标最常用（`PCMA`/`PCMU`） |
| G.722 / G.726 | ✅ | 部分国标设备 |
| AMR-NB / AMR-WB | ✅ | 手机录像 |
| 多路混音 | ❌ P2 | 需要重采样与增益归一，见 [video-wall.md](video-wall.md) §3.4 |

> **判定规则**：容器的「已支持」不代表其中每条流都支持。任一关键流不支持 → 该格 `src.unsupported`，文案必须带**编码名 + profile**（如「HEVC 10-bit 不支持硬解且软解不可用」），不允许只显示「播放失败」。

---

## 4. GB28181（国标，P1）

### 4.1 为什么必须单列

国产 NVR / 摄像头大量只提供「平台接入」而非裸 RTSP；GB28181 是**跨厂商**的统一入口。它不是 URL，而是 **SIP 信令 + RTP 媒体** 的会话，必须自研适配层。

### 4.2 两种接入模式（产品必须二选一，不能混着做）

| 模式 | 拓扑 | 优点 | 缺点 | 建议 |
|------|------|------|------|------|
| **A. 工具作「迷你平台」** | 设备/下级平台 `REGISTER` 到本工具（需监听 UDP/TCP 5060） | 不依赖客户已有平台 | 要改设备配置；要占端口；NAT/防火墙麻烦；与 [tools.md](tools.md) §9 的「不自开监听端口」冲突最大 | 不推荐 P1 首选 |
| **B. 工具作「上级平台客户端」** | 本工具作为 SIP 客户端接入客户**已有**平台（如海康 iVMS/大华 DSS/宇视），由平台转发 INVITE | 不改设备；只出站连接；权限面小 | 依赖客户平台账号与开放接口 | **推荐**（一期只做 B） |

> 若将来做 A，MUST 申请 `localMediaListen` 权限，且端口仅绑指定网卡、只承载 SIP/RTP，不承载任何 FlyScreen 信封（见 [video-wall.md](video-wall.md) §7 第 3、4 条）。

### 4.3 信令最小集（B 模式）

```text
1. REGISTER（带 Digest 认证，Expires=3600）→ 200 OK
2. MESSAGE / Keepalive（心跳，默认 60s）
3. MESSAGE / Catalog 查询 → Catalog Response（设备/通道列表，含通道 ID 与状态）
4. INVITE（SDP: s=Play, m=video <port> RTP/AVP 96, a=rtpmap:96 PS/90000, y=<SSRC>）
5. 200 OK → ACK → RTP 媒体到达（PS over RTP）
6. MESSAGE / BYE 或会话超时 → 停止
```

- **媒体**：RTP 载荷为 **MPEG-PS**（含 H.264/H.265 + G.711）。解复用直接交给内核的 `mpegps` demuxer（把 RTP 拆出的 ES 重新拼成连续 PS 字节流，或直接把 RTP 载荷串成流喂给 demuxer）。
- **SSRC**：必须按 INVITE 返回的 `y=` 字段过滤 RTP 源，否则多设备同端口会串流。
- **TCP 媒体**：2016/2022 支持 `a=setup` 走 TCP；UDP 丢包严重时切 TCP。
- **SDP 里的 `f=` 行**：部分国产设备必需，缺失会被拒绝；解析时不要丢弃未知行。

### 4.4 版本与差异

| 版本 | 差异 | 处理 |
|------|------|------|
| GB/T 28181-2011 | 老设备，字段少 | 兼容解析，缺字段给默认 |
| GB/T 28181-2016 | 主流；新增 TCP 媒体、编码标识 | **P1 基线** |
| GB/T 28181-2022 | 新增 H.265、更多能力集 | P2 增量；按 2016 跑通再扩 |
| GB35114（加密） | 证书 + 签名/加密视频 | **不支持**，明确报错 |

### 4.5 现场差异（这是真正的风险，不是代码）

| 现象 | 常见原因 | 对策 |
|------|----------|------|
| 注册成功但 INVITE 被拒（488/603） | SDP 字段不全、缺 `y=`、编码协商不匹配 | 保留原始报文日志开关；按厂商提供 SDP 模板微调 |
| 有信令无画面 | SSRC 不匹配、RTP 端口被防火墙拦、PS 头异常 | 抓包对照；SSRC 强校验；端口范围可配 |
| 画面花屏 | UDP 丢包、PS 时间戳断裂 | 切 TCP 媒体；增大缓冲；记录丢包率 |
| 只有音频无视频 | 设备只推 G.711（对讲通道）或视频编码未协商 | 明确区分「视频通道/对讲通道」 |
| 目录查不到通道 | 平台未同步、通道 ID 规则不同（20 位） | 支持手动填通道 ID |

**兼容性验收**：P1 必须用 **≥3 家真实设备/平台**（如海康、大华、宇视各一）跑通注册 + 目录 + 拉流，形成机型清单写入文档；只测一家不算通过。

---

## 5. ONVIF（P1）

| 能力 | 实现 | 备注 |
|------|------|------|
| WS-Discovery | SOAP over UDP → `239.255.255.250:3702`，`Probe` / `ProbeMatches` | 用户点「发现设备」才发，**禁止后台常驻扫描** |
| 设备信息 | `GetDeviceInformation`、`GetCapabilities` | 用于展示厂商/型号/固件 |
| Profile 列表 | `GetProfiles` | 主/子码流通常对应不同 Profile |
| 取流地址 | `GetStreamUri`（`RTP-Unicast` / `RTSP`） | 拿到后即普通 RTSP，交给 P0 播放路径 |
| 抓拍 | `GetSnapshotUri` | 可用于「空格缩略图」 |
| 认证 | WS-Security `UsernameToken`（nonce + created + SHA1 digest） | 与 RTSP 的 HTTP Digest 是两套，注意别混用 |
| PTZ | `ContinuousMove` / `Stop` / `GotoHome`（P2） | 只发标准命令 |
| 时间同步 | `SetSystemDateAndTime` | **MUST NOT 调用**（会改设备时间，属破坏性操作） |
| 固件升级 / 重启 | — | **MUST NOT** 实现 |

---

## 6. 地址归一化规则（MUST，实现时逐条对齐）

| 输入 | 处理 |
|------|------|
| 前后空白、全角冒号/斜杠 | 去空白；全角转半角（仅 scheme 与分隔符位置，路径不动） |
| 大写 scheme `RTSP://` | 统一小写 |
| 无 scheme 且路径存在 | 视为 `file` |
| 无 scheme 且路径不存在 | 报「无法识别的地址」，给出 3 个示例 |
| Windows 反斜杠路径 | 保留原样，不做 URL 编码 |
| 省略端口 | 按协议默认：`rtsp 554`、`rtmp 1935`、`http 80`、`https 443`、`srt 9000`（提示「使用默认端口」） |
| `user:pass@` 内嵌凭据 | 解析出凭据 → 转存凭据表 → 地址中剔除；UI 显示 `rtsp://***@host/…` |
| 查询参数含 `token/sign/key/auth/expires` | 视为签名地址：不落最近列表明文；提示有效期风险 |
| `udp://@239.x.x.x:port` | 组播标记，提示需网络支持 |
| 重复地址 | 添加时检测（同协议+同 host+同路径+同端口），提示「已存在：名称」，不静默去重 |
| 超长地址（>2048） | 拒绝并提示（可能是粘贴了整段 HTML） |

### 6.1 NVR 批量模板占位符

| 占位符 | 含义 | 示例 |
|--------|------|------|
| `{host}` | 主机 | `192.168.1.100` |
| `{port}` | 端口 | `554` |
| `{user}` / `{pass}` | 凭据（不写入地址明文，运行时注入 Header/URL） | `admin` |
| `{ch}` | 通道号（原样） | `1` |
| `{ch2}` | 通道号（两位补零） | `01` |

### 6.2 常见厂商 RTSP 路径（用于「预设」下拉，减少用户填错）

| 厂商 | 主码流 | 子码流 |
|------|--------|--------|
| 海康威视 | `rtsp://{host}:554/Streaming/Channels/{ch}01` | `…/{ch}02` |
| 大华 | `rtsp://{host}:554/cam/realmonitor?channel={ch}&subtype=0` | `…&subtype=1` |
| 宇视 | `rtsp://{host}:554/media/video1`（新固件 `/unicast/c{ch}/s0/live`） | `…/video2` |
| Axis | `rtsp://{host}/axis-media/media.amp` | `…?resolution=640x480` |

> 预设只是**快捷填充**，用户仍可改；不得写死为唯一路径。厂商固件差异极大，测试失败时给「换个路径试试」的提示而不是判死。

---

## 7. 探测（「测试」按钮）行为

| 协议 | 探测方式 | 超时 | 成功输出 |
|------|----------|------|----------|
| RTSP | `OPTIONS` → `DESCRIBE`（带认证重试） | 3s | 编码、分辨率（SPS 解析或 `a=fmtp`）、帧率（若有） |
| HTTP/HLS/DASH | `HEAD` 取 `Content-Type`/长度；HLS 再取首个 `m3u8` 解析分片时长 | 3s | 容器类型、是否加密（`EXT-X-KEY`）、预计首帧 |
| RTMP | 握手 + `connect` | 3s | 应用名、流名、是否可拉 |
| SRT | 建连握手（`mode=caller`） | 3s | 是否 listener 可达、建议 latency |
| file | 打开并读头部（前 1MB） | 1s | 容器、时长、编码 |
| GB28181 | 不走「测试」，走「注册状态 + 目录查询」 | 5s | 通道数、在线数 |
| ONVIF | `GetCapabilities` + `GetProfiles` | 3s | Profile 列表、是否支持 PTZ |

探测 MUST NOT 建播放会话、MUST NOT 落盘、MUST NOT 触发设备侧录像/云台动作。

### 7.1 首帧抓取路径（封面 + 可用性判定）

「测试」只验协议可达；**封面要走真正的解码路径**，因为用户要的是「这条配置到底能不能出画面」。两者分开实现，避免把「端口通」当成「能播」。

| 协议 | 抓首帧做法 | 超时 | 拿到即停 | 常见失败 |
|------|-----------|------|----------|----------|
| RTSP | `DESCRIBE` 取 SDP → `SETUP`（TCP 交织优先）→ `PLAY` → 解到第一个 IDR 帧 | 5s | 关 `TEARDOWN` 并释放解码器 | 401/403/404、只发 P 帧（无 IDR，等超时）、H.265 硬解不支持 |
| HLS | 取 `m3u8` → 选最低码率变体 → 拉第一个完整分片 → 解首帧 | 6s | 拉完一个分片即停 | 403 签名过期、分片 404、AES-128 缺 key |
| DASH | 取 `mpd` → 首个 `SegmentTemplate` 初始化段 + 首媒体段 | 6s | 同上 | `SegmentBase` 异常、无 init 段 |
| RTMP | 握手 + `connect` + `createStream` + `play` → 首帧 | 5s | 收到首帧即断开 | 流名错误、需要 `tcUrl` 鉴权 |
| SRT | `mode=caller` 建连 → 收首包 → 组帧解首帧 | 6s | 解出即断 | listener 未开、latency 设置过小导致丢包 |
| UDP/组播 | 加入组播组 → 收 RTP → 解首帧 | 5s | 解出即离开组播 | IGMP 未开、SSRC 不匹配 |
| 本机文件 | 打开 → seek 到首个关键帧（或直接解码前若干帧） | 2s | 解出即关 | 路径失效、无权限、容器损坏 |
| GB28181（P1） | INVITE → 200/ACK → RTP(PS) → 解首帧 → BYE | 8s | 解出即 BYE | 488 协商失败、SSRC 不匹配、PS 头异常 |

**实现约束**：

1. 抓帧 MUST 复用与播放相同的后端（同一 `IMediaBackend`），只是「取一帧就停」，MUST NOT 另建第二套解码器——否则「封面成功但播放失败」会变成常态。
2. 抓帧 MUST NOT 影响正在播放的同一源（若该源正在播放，可直接从现有会话取最近一帧，成功则跳过临时会话）。
3. 抓帧失败 MUST 映射到 [video-wall.md](video-wall.md) §8 的错误码（`net.timeout` / `auth.required` / `src.unsupported` / `decode.failed`），并在 UI 显示具体原因，不允许只显示「失败」。
4. 抓帧结果 MUST 写入记录（`validationState` / `coverKey` / `coverCapturedAt`），供列表/卡片状态点使用。
5. 并发 MUST ≤ 4（`docs/video-wall.md` §3.1.1），批量 NVR 抓帧同样受限。

---

## 8. 不支持清单（对外必须能答）

| 类别 | 具体 | 处理 |
|------|------|------|
| DRM | Widevine、PlayReady、FairPlay、Netflix/爱奇艺类加密 | 明确报「不支持受 DRM 保护的内容」 |
| 国标加密 | GB35114 A/C 级 | 明确不支持 |
| 新编码 | H.266/VVC、MPEG-H | 明确不支持（P2 评估） |
| 私有协议 | 厂商 SDK 直连（非 RTSP/GB28181/ONVIF） | 引导改用标准协议 |
| 私有封装 | 非标 PS 变体、私有加密帧 | 报 `src.unsupported`，附容器/载荷信息 |
| 极端规格 | 8K、>120fps、10-bit 4:2:2 多路 | 允许单路尝试，明确提示性能与解码限制 |
| 互动能力 | 云台之外的设备配置（改 IP、升级固件、重启、改时间） | **不实现**（破坏性） |

---

## 9. 端口与网络要求（部署前核对）

| 方向 | 协议 | 端口 | 备注 |
|------|------|------|------|
| 出站 | RTSP | 554 / 自定义 | TCP 控制 + RTP（TCP 交织或 UDP 高端口） |
| 出站 | HTTP(S)/HLS/DASH | 80 / 443 | 企业网需放行 CDN 域名 |
| 出站 | RTMP(S) | 1935 / 443 | — |
| 出站+入站 | SRT | 9000（可配） | `caller` 出站；`listener` 需入站（P1） |
| 入站 | RTP（组播/单播） | 动态 / 组播组 | 组播需交换机 IGMP |
| 出站 | SIP（GB28181 B 模式） | 5060 UDP/TCP | 仅出站注册 |
| 入站 | SIP + RTP（A 模式） | 5060 + 30000–30500 | 需 `localMediaListen` |
| 局域网 | ONVIF 发现 | UDP 3702 组播 | 仅手动触发 |

> 企业网络常封 UDP 高端口 → RTSP 优先用 **RTP over TCP**；这一点必须做成默认，而不是让用户自己猜。

---

## 10. 验收（本文件对应部分）

- [ ] P0 协议矩阵中每个 ✅ 项至少一条真实地址跑通（含认证、含 403/404 负例）。
- [ ] 编码矩阵中每个 ✅ 项至少一条样本跑通，并在 UI 正确显示编码名与硬/软解。
- [ ] 地址归一化 12 条规则逐条有单测。
- [ ] NVR 批量模板：`{ch}` / `{ch2}` 正确生成 16 路并全部可测。
- [ ] 三家厂商预设路径至少各跑通一家真实设备。
- [ ] 不支持清单中的每类都能给出确定文案（不是「播放失败」）。
- [ ] P1：GB28181（B 模式）≥3 家真实设备/平台跑通注册 + 目录 + 拉流，机型清单入库。
- [ ] P1：ONVIF 发现 + 取流跑通，且确认**未**调用任何写设备接口（抓包 + 代码审查）。
