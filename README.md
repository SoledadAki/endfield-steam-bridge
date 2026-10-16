# Endfield Steam Bridge

让《明日方舟：终末地》官服或 Bilibili 服通过 Steam 游戏库启动，无需下载国际服。

下载程序：进入本仓库的 **Releases**，下载 `EndfieldSteamBridge-0.2.3-win-x64.zip`，解压运行 `EndfieldSteamBridge.exe`。

## 主要功能

检测当前 Steam 账号是否拥有终末地，以及本机官服 / Bilibili 服客户端。

选择服务器后，一键载入，自动打开对应启动器并开始游戏。

支持 Steam 游玩时长、Shift+Tab 叠加层和 F12 截图。

一键撤回载入；原本没下载国际服，恢复 Steam 的“安装”状态。

## 简单使用

先登录 Steam 并将终末地加入库，安装并登录要玩的国服。退出游戏和鹰角启动器后，以管理员身份运行本工具，选择“官服”或“Bilibili 服”，确认路径并点击“一键载入国服”，之后从 Steam 开始游戏即可。

需要恢复时，退出游戏和启动器，点击“撤回载入国服”。切换服务器也请先撤回，再选择另一服务器载入。

适用于 Windows 10/11 x64，需要 .NET Framework 4.8。Steam 也需以管理员身份运行。

代码采用 MIT 开源协议。
