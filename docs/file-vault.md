# 文件保险箱（file-vault）

| 项 | 内容 |
|----|------|
| ToolId | `file-vault` |
| kind | `enhancement` |
| crossScreen | `false` |
| group | `utilities` |
| hasWorkspace | `true` |
| 设备 | Desktop（Windows / macOS / Linux） |
| 工程 | `src/Tools/FileVault/` |

关联：[tools.md](tools.md)。

## 目标

将本机文件/文件夹移入加密保险箱；锁定后外部只能看到密文与 UUID，无法查看真实文件名与内容。浏览与打开仅在解锁会话内；可按需挂载到临时目录供系统程序打开；支持整库迁移到其他磁盘；卸载时必须选择：解密保留 / 永久销毁 / 仅卸工具保留密文。

## 密钥模型（默认）

**默认不做 DeviceSecret / 一机一码。** 安全依赖：

1. 每个保险箱创建时用 CSPRNG 生成 **MK** 与 **salt**（不在插件 DLL 内）  
2. 用户 **主密码** 经 PBKDF2-SHA256 派生 KEK，包装 MK → `keywrap.bin`  
3. 打开保险箱 **必须** 输入主密码；丢失密码且无明文备份 → 数据不可恢复  
4. 开源可审计算法（AES-GCM / PBKDF2）；安全不靠隐藏算法或混淆  

换机 / 重装：拷贝 VaultRoot（或 `.flyvault`）+ 记得主密码即可打开。

可选后期：「绑定本机」高级选项（须强制 Recovery Key）——**本期不做**。

## 存储

```text
{VaultRoot}/
  vault.manifest.json     # 非敏感：schema、id、KDF、统计
  keywrap.bin             # 口令包装的主密钥 MK
  vault.db.enc            # 加密索引
  objects/<uuid>.blob     # 分块密文
```

`ToolData/file-vault/settings.json`：`activeVaultRoot`、`autoMount`（默认 **false**）、`idleLockMinutes`、`secureDeleteSource`。

默认 VaultRoot：`%LocalAppData%\FlyScreen\Vaults\default`（不在安装目录内，避免升级/卸载误删）。若仍存在旧版 `ToolData/file-vault/vault` 则继续沿用。

## 密码学

- 口令 → PBKDF2-SHA256（默认 600_000 迭代，写入 manifest）→ KEK  
- 创建时口令强度：至少 8 字符（UI + 服务双检）  
- MK：256-bit 随机；`keywrap = AES-GCM(KEK, MK)`  
- 每文件独立 DEK；内容 AES-256-GCM 分块（默认 1 MiB）  
- 索引 JSON 整体用 MK 封装  

## 挂载

默认 **不** 在解锁后自动全量物化（大库慢、占盘）。用户手动「挂载」后，将虚拟树解密到 ACL 尽量收紧的临时目录；锁定时安全擦除。单文件亦可通过临时解密打开。

## 迁移

锁定 → 校验目标空间 → 同卷 Move / 跨卷复制校验 → 更新 settings 指针 → 失败回滚。支持导出/导入 `.flyvault` 备份包（密文整包）。

## 解耦边界

主程序 / 安装器 / 卸载器 **不得** 引用或特判 `file-vault`（无 Vault 探测、无离线解密、无保险箱专用 UI）。  
宿主只通过 ToolSdk（`IToolModule` / `IToolUninstallPolicy`）与通用 `Tools/` 目录约定交互；Vault 逻辑全部在本工具包内。

## 卸载（MUST）

本工具通过通用契约参与卸载，宿主/卸载器不特判 `file-vault`：

- 包内 [`uninstall-ui.json`](../src/Tools/FileVault/FlyScreen.Tool.FileVault/uninstall-ui.json)（与 `GetUninstallUiSchema()` 同形）  
- `IToolUninstallPolicy.ApplyUninstallAnswersAsync` 解读 `action`=`keepEncrypted`|`decryptKeep`|`destroy` 等答案  

### 工具页卸载

宿主按 schema 弹出**通用**决策框 → Apply：

1. **解密并保留到文件夹…** — 解锁 → 全量解密 → 删 VaultRoot → 继续卸工具  
2. **永久销毁** — 确认字样 → 擦除 VaultRoot → 卸工具  
3. **仅卸载工具，保留加密数据** — 不删 VaultRoot  
4. 取消 — 中止卸载  

### 产品卸载 / 升级（宿主侧，与本工具解耦）

- **升级**：`ToolsUserState` 合并 Installed/ToolData 等，不解析工具内容。  
- **卸载器**：列出已装插件；用 `uninstall-ui.json` 收集决策后在 **Uninstall.exe 进程内离线执行**（禁止启动玄鸟主程序）。`keepEncrypted` 保留保险箱目录；`destroy` 删除 ToolData 与 `activeVaultRoot`；`decryptKeep` 须先在应用内解密，卸载器不拉起主程序。  
- 默认 VaultRoot 在 `%LocalAppData%\FlyScreen\Vaults\default`；若选「保留加密」且不卸该插件，产品卸载后库仍在。  

## 威胁边界

- SSD/TRIM 下安全删除无法保证物理层不可恢复  
- 不防已解锁会话的内存调试 / 同用户恶意程序  
- 产品文案不得宣称「绝对防取证」或「反编译也看不到算法所以安全」  
