FlyScreen 虚拟显示驱动离线包（共用）
====================================
路径：tools\VddOffline\（工具/资源，不在 src 代码树）

Win 主机（WPF / Maui-Win）构建时复制到程序输出 VddOffline\。
Mac / Linux 仅复制本 README（本期不发起扩展、不装 MTT）。

随包（Windows）：
  Virtual.Display.Driver-*-setup-x64.exe
  VDDControl-*.exe

拉取：
  powershell -File docs\mtt_vdd\fetch-offline-pack.ps1

顺序：setup → winget →（按需下载）Driver.Only → 启动 VDDControl
