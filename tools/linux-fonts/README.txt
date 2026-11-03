Linux CJK 开源字体（方案 B：随包）
====================================
本目录字体随 FlyScreen Linux 构建复制到输出 Fonts/。

当前文件：
  NotoSansSC-Regular.otf  — Noto Sans SC Regular（简体子集，SIL OFL）
  OFL.txt                 — 许可全文
  README.txt              — 本说明

来源：https://github.com/notofonts/noto-cjk （Sans/SubsetOTF/SC）
重新拉取：powershell -File docs\linux_fonts\fetch-noto-sc.ps1

禁止放入：微软雅黑 / 黑体（msyh / simhei）等 Windows 专有字体。
说明：docs/THIRD_PARTY.md 、 docs/runbooks/build-linux.md
