# 视频墙：可行性分析（内核选型 / 性能 / 许可 / 风险）

| 项 | 内容 |
|----|------|
| 性质 | 可行性分析 + 技术选型建议，不替代实现规格 |
| 日期 | 2026-11-13 |
| 配套 | 需求与验收见 [video-wall.md](video-wall.md)；界面与数据模型见 [video-wall-prototype.md](video-wall-prototype.md)；协议/编码矩阵见 [video-wall-sources.md](video-wall-sources.md) |
| 结论 | **可行**。一期用 **libVLC（LibVLCSharp）** 换「格式/协议覆盖 + 快速落地」；二期用 **FFmpeg（libav）** 换「单画布合流 + 单流输出 + 更好的多路资源控制」。两者放在同一个 `IMediaBackend` 后面，允许分阶段替换，不必一次到底 |

---

## 0. 一句话结论

**「9 路播放」不难，「9 路长期稳定 + 真合流 + 覆盖国内摄像头生态」才是难点。**
前两者决定内核选型，第三者决定要不要自研 SIP/ONVIF 适配层。

- 只做「九宫格能看」：libVLC，4~5 周可出可用版本。
- 要做「单画布合流 + 可输出单流」：必须有 FFmpeg 合成路径，+4 周。
- 要做「国产 NVR/摄像头开箱即用」：必须补 GB28181，+3 周（且现场差异是主要风险，不是代码量）。

---

## 1. 现状盘点（本仓库已有能力，能复用多少）

| 现有能力 | 位置 | 对本工具的价值 |
|----------|------|----------------|
| 工具宿主：`IToolModule`、卡片、工作区、ALC 加载 | `src/FlyScreen.ToolSdk`、`src/FlyScreen.Core/Services/Tools` | **直接复用**，本工具只是又一类 `enhancement` |
| `.flytool` 打包与安装（含 SHA256 信任、`runtimes/<rid>/`） | `build/Pack-FlyTools.ps1`、`ToolInstaller` | **直接复用**，但需处理 90MB 级包体（§6） |
| 远程索引 + 下载缓存 | `ToolRemoteIndexClient` | 大包不走 `Bundled` 的关键通道，已具备 |
| 三端平台 UI 卫星模式（`Ui.Win/.Desktop/.Maui`） | `src/FlyScreen.Tool.*` | **直接复用**（渲染面嵌入方式需各自适配） |
| 图像编解码/JPEG 帧通道 | `SharedDevicesMediaCodec`、投影 JPEG 通道 | 与本工具无关。**没有**音视频解码内核可复用 |
| 原生依赖打包经验（VDD 离线包） | `docs/mtt_vdd`、`tools/VddOffline` | 可参考其「大二进制随包」的组织方式 |

**关键事实**：仓库当前**没有任何** FFmpeg / libVLC / MediaFoundation 播放代码，全部为图像帧（JPEG）与 PCM 音频。视频墙的内核是**净新增**，不存在「顺手复用」的余地——这决定了工作量与许可工作量都必须单独立项。

---

## 2. 内核选型

### 2.1 候选对比

| 维度 | **libVLC 3.0（LibVLCSharp）** | **FFmpeg（libav 自绘播放器）** | GStreamer | libmpv | OS 原生（MF/AVF/VA） |
|------|------------------------------|------------------------------|-----------|--------|----------------------|
| 协议覆盖 | 极强：file/http(s)/HLS/DASH(基础)/RTSP/RTP/UDP/RTMP(S)/SRT/SMB/FTP/MMS | 极强但需自己接：http(s)/HLS/DASH/RTSP/RTMP/RTP/SRT（SIP 需自建） | 强，靠插件（rtspsrc/hlsdemux/dashdemux/rtmpsrc/souphttpsrc） | 强（mpv 本身基于 ffmpeg） | **弱**：MF 无 RTSP/HLS 原生；AVF/VA 更偏本地文件 |
| 编码覆盖 | 极强（内置 ffmpeg + dav1d，H.264/H.265/AV1/VP9/ProRes/RV40/VC-1…） | 极强（选对构建即可） | 强 | 极强 | 中：HEVC/AV1 依赖系统扩展与硬件 |
| 多路同窗 | 每 `MediaPlayer` 一个独立 vout 窗口 → 9 个 HWND | **单交换链合成 N 路纹理**（真合流） | `compositor` 原生支持多路合成 | 每实例一个窗口，9 实例重 | 各自一套，跨平台三套代码 |
| 合流输出（单流） | 不做（需另配编码器） | **天然可做**（`avfilter` + 硬件编码器） | 可做（`compositor` + 编码 + `rtspclientsink`） | 可做（`lavfi` 滤镜图）但不易精细控制 | 难 |
| 硬解 | D3D11VA/DXVA2、VideoToolbox、VAAPI（成熟） | D3D11VA/DXVA2/VideoToolbox/VAAPI（需自己写 hw_frames_ctx 与纹理映射） | d3d11/nvcodec/vaapi（插件质量参差） | 成熟 | 系统级，最省心但覆盖窄 |
| 嵌入 WPF/Avalonia/MAUI | `LibVLCSharp.WPF` / `.Avalonia` 有现成 `VideoView`；MAUI 无官方视图（需 `SetParent`/`SurfaceView` 适配） | 自己写子窗口 + 交换链（三端都要写） | 需自建 sink 窗口 | `--wid` 嵌入，简单 | WPF `MediaElement`/`SwapChainPanel` |
| 许可 | libVLC：**LGPL-2.1**（动态链接可商用）；注意官方 Windows 构建含部分 GPL 插件（x264 等） | **LGPL-2.1**（用 `lgpl-shared` 构建；禁用 `--enable-gpl`） | LGPL 主体 + 混合插件 | **GPLv2+**（默认构建，传染风险高） | 随系统，无额外许可 |
| 包体（win-x64，压缩前） | ~90–120MB（可裁剪插件到 ~45–60MB） | ~60–90MB（shared，lgpl） | ~120–180MB + 插件注册 | ~40–60MB | 0（随系统） |
| .NET 接入成熟度 | 高（官方 NuGet，WPF/Avalonia 视图现成） | 中（`FFmpeg.AutoGen` 绑定 + 自研封装） | 低（P/Invoke + 插件管理复杂） | 中（社区绑定） | 高但平台分裂 |
| 开发量（到九宫格可用） | **低**（4~5 周） | 高（8~10 周，含时钟/同步/seek） | 中高（6~8 周，部署与插件坑多） | 中（5~6 周，但许可不可接受） | 中，但只覆盖 Windows 且协议不全 |
| 主要坑 | WPF airspace、9 个 HWND 开销、无真合流 | 一切都要自己写（音画同步、seek、缓冲） | Windows 部署体积与插件路径 | GPL 传染 | 协议覆盖不足，无法满足「所有地址」 |

### 2.2 选型结论

| 阶段 | 内核 | 理由 |
|------|------|------|
| **P0** | **libVLC 3.0 + LibVLCSharp** | 用最小代价拿到「几乎所有地址都能播」，把工期留给布局、源管理、稳定性这些产品价值更高的部分 |
| **P1** | **FFmpeg（LGPL shared）** | 只有它能低成本做到「单交换链合成 + 合流编码输出 + 更精细的多路资源控制」 |
| 不做 | GStreamer | 能力强但 Windows 部署与插件治理成本过高；团队规模撑不住两套原生栈 |
| 不做 | libmpv | GPLv2+ 对闭源分发不友好；且同样没有单画布合成 |
| 只作兜底 | OS 原生（MF） | 可作为「本机文件轻量播放」的备用路径，不作为主内核 |

### 2.3 反面意见（必须写清楚，避免只报好消息）

- **双后端 = 双份维护**：两套原生库、两套硬解回落逻辑、两套错误映射。若团队只有 1 人且时间充足（≥12 周），**直接上 FFmpeg 单后端**反而更省长期成本，代价是一期交付晚 4~6 周，且没有 libVLC 那样现成的 WPF 视频控件。
- **libVLC 的「格式覆盖」是借来的**：一旦某个格式在 VLC 里表现异常（例如某些私有 PS 封装、非标 HLS），排查要下沉到 VLC 插件层，成本高于 FFmpeg（后者可以自己控制 demux 参数）。
- **9 个 HWND 的抗压性未知**：窗口数量本身不是问题，但每个 vout 都带独立的 D3D 交换链与呈现线程，长期运行（数小时）的稳定性必须实测，不能靠推断。

---

## 3. 性能可行性（预算表，估算值，须 PoC 实测）

### 3.1 解码

| 场景 | 单路成本（估算） | 9 路合计 | 结论 |
|------|------------------|----------|------|
| 1080p25 H.264 软解 | 0.1–0.2 核 | 1–2 核 | 现代 6 核以上桌面可承受 |
| 1080p25 H.265 软解 | 0.25–0.5 核 | 2.5–4.5 核 | 需 8 核以上，建议开硬解 |
| 4K25 H.265 软解 | 1–1.5 核 | 4 路即 4–6 核 | **必须硬解**，且要限制路数 |
| 1080p25 硬解 | 接近 0（GPU 解码单元） | 受**并发解码会话数**限制 | 见 §3.2 |

### 3.2 硬件解码会话上限（这是最容易翻车的地方）

| 厂商 | 常见上限（经验值，随驱动/型号变化） | 备注 |
|------|--------------------------------------|------|
| Intel iGPU（QSV） | 8–16 路 1080p | 较宽松；核显共享内存带宽，4K 多路会掉帧 |
| NVIDIA 消费卡（NVDEC） | 历史上 5 路并发（部分型号/驱动已放宽） | **必须以实机为准**；超出时后续解码回落软解 |
| AMD（VCN/UVD） | 8–16 路 | 同样受显存带宽约束 |
| Apple（VideoToolbox） | 与芯片/机型相关 | Mac 端二期再测 |

**设计对策**（MUST 落到实现）：

1. 硬解会话池：申请失败 → 该格回软解，并在 UI 标注「软解」角标。
2. 总预算闸门：按「分辨率 × 帧率 × 编码 × 硬/软」估算权重，超过阈值提示降级。
3. 非焦点格降帧：合流/大屏模式下非焦点格可降到 10–15fps 渲染（解码仍按源帧率），减少 GPU 呈现压力。

### 3.3 内存与显存

| 项 | 估算 | 说明 |
|----|------|------|
| libVLC 单实例固定开销 | 40–60MB | **必须单实例多播放器**，9 个实例会多出 300MB+ |
| 每路解码/缓冲 | 30–60MB | 与缓冲时长、分辨率强相关 |
| 9 路进程内存合计 | 400–600MB | 4K 场景显著更高 |
| 显存（1080p NV12） | 每路 20–40MB（含队列） | 9 路约 200–350MB |
| 建议机器下限 | 4 核 / 8GB / 支持硬解的 GPU | 9 路 4K 需 8 核+16GB+独显 |

### 3.4 网络与首帧

| 项 | 数值 | 说明 |
|----|------|------|
| 1080p H.264 RTSP | 2–6Mbps/路 | 9 路约 18–54Mbps |
| HLS 直播课 | 1.5–5Mbps/路 | 分片时长 2–6s，首帧慢 |
| 千兆局域网 | 必需 | 2.4G Wi-Fi 无法承载 9 路；5G Wi-Fi 需实测丢包 |
| RTSP 首帧 | 0.5–2s | 取决于 IDR 间隔与缓冲策略 |
| HLS 首帧 | 2–5s | 取决于分片时长（`EXT-X-TARGETDURATION`） |
| GB28181 首帧 | 3–8s | SIP 注册/INVITE + PS 流建立 |

**缓冲策略**：RTSP 默认 500–1000ms 抗抖动缓冲，可配「低延迟（200ms）/稳定（1500ms）」；HLS 用最小分片预取。

### 3.5 包体与安装

| 项 | 估算 | 对策 |
|----|------|------|
| libVLC Windows 全量插件 | 90–120MB | 裁剪：去掉编码类插件、无关输出/界面模块，保留 `demux/access/codec/video_output/audio_output` → ~45–60MB |
| FFmpeg `lgpl-shared` | 60–90MB | 只带必要 DLL（`avformat/avcodec/avutil/swscale/swresample/avfilter`） |
| `.flytool` 压缩后 | 30–55MB | ZIP deflate；原生 DLL 压缩率一般 |
| 内置 `Bundled` | **不建议** | 每个 App 输出目录都会多背一份；走 Release 下载（§6） |

---

## 4. 渲染可行性（WPF / Avalonia / MAUI 三端差异）

| 端 | P0（libVLC） | P1（FFmpeg） | 已知限制 |
|----|--------------|--------------|----------|
| WPF（`App.Windows`、`Ui.Win`） | `LibVLCSharp.WPF.VideoView`（每格一个 `HwndHost`） | 自建子 HWND + D3D11 交换链 | **airspace**：原生视频面之上不能叠任意 WPF 控件，控制条/角标要用独立浮层窗口或放在格外的工具条 |
| Avalonia（`App.Desktop`、`Ui.Desktop`） | `LibVLCSharp.Avalonia.VideoView`（`NativeControlHost`） | 同上（子 HWND + 交换链） | Avalonia 11 的 `NativeControlHost` 对 z-order 同样敏感，需同款浮层策略 |
| MAUI/WinUI（`Ui.Maui`） | 无官方视图，需 `SetParent` 把 vout 子窗口贴到 `SwapChainPanel` 对应区域 | `SwapChainPanel` 原生支持 | 一期不做 MAUI（清单 `platforms: ["Windows"]` 会把 WinUI 也纳进来，需在 `featuresByDeviceClass` / UI 侧明确「Maui 一期不展示」） |
| Android | libvlc-android AAR + `SurfaceView` | 极难（NDK 级） | 二期评估；竖屏最多 4 路 |

**结论**：**Windows 桌面（WPF + Avalonia）是一期的唯一交付面**。Maui/Android 必须明确排除，否则「三端同构」会把工期翻倍且质量不可控——这与 [tools.md](tools.md) §1.1 第 6 条的「同构」要求需要在评审时显式豁免并记录理由。

---

## 5. 许可可行性（分发前必须过）

| 组件 | 许可 | 分发要求 | 本项目的动作 |
|------|------|----------|--------------|
| libVLC / libvlccore / 插件 | LGPL-2.1 | 动态链接、附许可文本、提供替换库的方法（不静态链接） | 在 `runtimes/win-x64/` 内置 DLL + `LICENSE-libvlc.txt` + 「可替换原生库」说明 |
| LibVLCSharp（托管封装） | LGPL-2.1 | 同上（托管程序集引用） | 附许可文本 |
| VLC 官方构建中的部分插件 | **GPLv2**（如 x264 编码、部分可视化） | 若分发含 GPL 插件，需按 GPL 提供对应源码/书面要约 | **打包时剔除**这些插件（本项目不需要 VLC 编码），并在打包脚本里维护剔除清单 |
| FFmpeg | LGPL-2.1（`lgpl-shared` 构建） | 动态链接、附许可、提供源码获取地址 | 使用 BtbN `win64-lgpl-shared` 类构建；MUST NOT 启用 `--enable-gpl` |
| 硬件编码器（NVENC/QSV/AMF/VideoToolbox） | 各自 SDK/EULA，经 FFmpeg 运行时调用 | 无需再分发 SDK | 合流输出阶段注意驱动 EULA 与「不得用于转码服务」条款 |
| SkiaSharp（若做 CPU 合成兜底） | MIT | 附许可 | 可选 |
| SIPSorcery（GB28181 SIP 栈） | MIT | 附许可 | P1 |

**待办**：`docs/THIRD_PARTY.md` 增加上述条目；`third_party/licenses/` 放许可全文；`Pack-FlyTools.ps1` 输出「包内许可清单」便于审计。

> 开源项目（本仓库）对 GPL 的容忍度更高，但仍建议**剔除 GPL 插件**：既减小包体，也避免用户在企业环境分发时被许可问题卡住。

---

## 6. 大包与 `.flytool` 的冲突（必须先决）

现实：`Tools/Bundled/*.flytool` 会被复制进每个 App 输出目录（`Pack-FlyTools.ps1` 与 MSBuild 目标），一个 50MB 的包会让每个安装包都变重，且 `artifacts/` 体积翻倍。

| 方案 | 做法 | 评价 |
|------|------|------|
| A. 内置 Bundled | 与现有两个工具一致 | **不推荐**：每次发行多背 30–55MB，且与本工具「非核心能力」的定位不符 |
| B. 远程索引 + 按需下载 | `tools-index.json` 增加条目（`url`/`giteeUrl`/`sha256`），工具页显示卡片，点安装才下载 | **推荐**。`ToolRemoteIndexClient` 已实现，`ToolIndexEntry` 已带 `nameKey/summaryKey` |
| C. 独立分发页 | 只在 GitHub Release 提供，工具页不展示 | 体验差，用户找不到 |

**前置条件**（需宿主小改）：`ToolCatalog` 目前以 `Bundled` 目录为准，需支持「远程索引 → 卡片」这条路径；否则卡片根本不显示，方案 B 落不了地。此项列入 [video-wall.md](video-wall.md) §7 增量第 6 条。

---

## 7. 风险与对策

| # | 风险 | 概率 | 影响 | 对策 |
|---|------|------|------|------|
| R1 | 「支持所有格式」被当真，遇到 DRM/私有封装就被判不合格 | 高 | 口碑 | 分级矩阵 + 明确「不支持清单」；错误分类里单列 `src.unsupported` 并给出编码名 |
| R2 | 9 路硬解超出 GPU 会话上限，部分格静默回软解导致 CPU 打满 | 高 | 体验 | 硬解会话池 + 软解角标 + 预算闸门 + 实测机型清单 |
| R3 | WPF airspace 导致角标/控制条盖不住视频 | 高 | UI 返工 | P0 就用「格内不叠控件」的布局：控制条在格下方或全局工具条；角标用格外部徽标 |
| R4 | VLC 包内含 GPL 插件，企业分发被卡 | 中 | 合规 | 打包剔除 + 许可清单 + `THIRD_PARTY` 更新 |
| R5 | 包体过大，用户安装/分享体验差 | 中 | 转化 | 裁剪插件、按 RID 分包、走 Release 下载 |
| R6 | GB28181 现场差异（私有扩展、UDP/TCP 模式、SSRC、编码 G.711 变体） | 高 | 交付 | P1 才做；先支持「注册+目录+INVITE+PS/H.264/G.711」最小集；提供报文日志开关；用 2~3 家真实 NVR 做兼容清单 |
| R7 | 进程内原生解码崩溃拖垮主程序 | 中 | 严重 | P0 接收风险并记录；P1 外置引擎进程；对已知崩溃做「黑名单源 + 自动降级」 |
| R8 | HLS 直播课首帧慢/延迟大，被当成「卡」 | 中 | 体验 | 明示「直播延迟 2–6s 属正常」；提供低延迟缓冲选项 |
| R9 | 三端 UI 同构要求把 Android 拖进来 | 中 | 工期翻倍 | 清单只声明 Windows；评审时显式豁免并写理由 |
| R10 | 凭据/URL 泄漏到日志或导出文件 | 中 | 安全 | 统一脱敏器 + 导出默认去凭据 + 代码评审检查点 |
| R11 | 用户把内网任意地址填进来，工具成了「内网探测跳板」 | 低 | 安全 | 只播用户显式地址；无扫描 API；ONVIF 发现需手动触发 |

---

## 8. PoC 计划（先花 3~5 天验证，再决定是否开 8 周）

目标：把「会不会白干」的三个未知先打掉。

| # | 试验 | 通过标准 | 失败则 |
|---|------|----------|--------|
| 1 | 单窗 9 个 libVLC 子窗口播 9 路 1080p H.264 RTSP，连续 2 小时 | 无崩溃、CPU < 50%、内存漂移 < 10%、每格可独立重连 | 缩到 4 路或提前引入 FFmpeg 单画布 |
| 2 | 同机 4 路 4K HEVC（硬解） | 帧率 ≥ 24fps、无花屏、GPU 解码会话未超限 | 限制 4K 路数为 2，或加「仅 1080p」约束 |
| 3 | WPF `VideoView` 内叠加角标/菜单 | 不出现黑块/穿透（或确认 airspace 限制可绕过） | 改布局（控制条外置） |
| 4 | 包体：裁剪插件后的 `.flytool` 实际大小 + 安装耗时 | ≤ 60MB、安装 < 20s | 分包 / 移除外置模型 |
| 5 | 1 台国产 NVR 的 RTSP 直取 + 1 台 ONVIF 取流 | 都能出画面；Digest 认证通过 | ONVIF/GB28181 降级为 P2 |

> PoC 代码可直接写在 `src/FlyScreen.Tool.VideoWall/Backend/Vlc/` 下，作为正式实现的起点，不另起仓库。

---

## 9. 工作量汇总（1 人，Windows 桌面）

| 阶段 | 内容 | 估时 |
|------|------|------|
| PoC | §8 五项 | 3–5 天 |
| P0 | 骨架 + 源管理 + libVLC + 九宫格 + 场景 + 打包 | 8 周 |
| P1 | FFmpeg 单画布与合流输出 | +4 周 |
| P1 | ONVIF | +1.5 周 |
| P1 | GB28181 | +3 周（可与 FFmpeg 并行） |
| P2 | 推流输出 / 混音 / mac / Linux / Android | 各 2–6 周 |

**关键路径**：PoC（1 周）→ P0（8 周）→ FFmpeg（4 周）。ONVIF/GB28181 可与 FFmpeg 并行，但需要真实设备，建议提前备机。

---

## 10. 结论

| 问题 | 回答 |
|------|------|
| 技术可行吗 | 可行。播放内核、嵌入方式、硬解回落、许可路径都在成熟方案里 |
| 最大不确定性 | 9 路长期稳定性（HWND 数量 + 硬解会话）与 GB28181 现场兼容性，两者都只能靠实测收敛 |
| 建议的第一步 | 用 3–5 天做完 §8 的 PoC；PoC 通过再按 P0 排 8 周，不要在没测过 9 路之前写完整 UI |
| 建议不做的 | 不在 P0 承诺 Android/mac/Linux；不在 P0 承诺码流级合流；不在 P0 承诺帧级同步 |
