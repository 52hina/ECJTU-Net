# 校园网自动登录 ECJTU-Net
（全由DeepSeek完成，我在自己电脑上运行的还行，不愿意就别用，不保证全都能用，可以自己找DeepSeek改）
开机 / 掉线时自动登录校园网门户（Dr.COM ePortal 网页认证），不用每次打开浏览器手动输账号密码。

## 功能

- 开机自动登录、掉线自动重连（后台守护，最低优先级、约 6~7MB 内存、CPU 几乎为 0）
- 有线优先：插网线自动走有线认证；没网线自动找同名校园 WiFi 并连接（没保存过这个网络也会自动创建配置）
- 不在学校自动识别：连的是家里等其它网络时不认证、直接退出
- 设置界面 + 托盘图标（立即重连 / 打开设置 / 查看日志 / 退出守护），密码可勾选明文查看
- 开机自启（启动文件夹快捷方式，不写注册表）、一键卸载脚本
- 日志只保留最近 30 天；密码用 Windows DPAPI（当前用户）加密保存
- 认证失败时记录 Dr.COM 返回码（RetCode=1/2/7/512 等），方便排查

## 使用（下载 Release）

1. 下载 Release 里的 zip，解压到一个文件夹（如 `D:\CampusNet`）
2. 双击 `ECJTU-Net.exe` → 填账号 / 密码 / 运营商 → 勾选需要的选项 → 保存
3. 一般勾选「启用自动连接」「开机自启动」「掉线自动重连」即可
4. 想删除：双击 `卸载.bat`

## 自行编译

不需要安装任何 SDK，用 Windows 自带的 .NET Framework 4 编译器即可：

双击 `build.bat`，或手动执行：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 ^
  /target:winexe /platform:anycpu /optimize+ ^
  /out:dist\ECJTU-Net.exe /win32icon:src\ECJTU-Net-small.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.Security.dll src\Program.cs
```

## 说明与免责

- 本程序**没有数字签名**。Windows 的「智能应用控制 (Smart App Control)」或部分杀毒软件可能
  报"未知程序/误报"，请加白名单，或关闭 Smart App Control（Windows 安全中心 → 应用和浏览器控制 → 智能应用控制设置）。
- 密码用 Windows DPAPI 加密保存在程序目录的 `ECJTU-Net.ini`，仅本机本用户可解密。
- 程序只做与你在浏览器里手动登录相同的事（向校园网认证服务器提交账号密码），不会向任何其它地址上传信息。
- 认证服务器地址 / AC 参数默认按某高校 Dr.COM 门户配置（`172.16.2.100` / `ecjtu_nic_ME60`）；
  其它学校的 Dr.COM 门户可能需要在 `src\Program.cs` 的 `Net.PortalLogin` 中修改对应常量。
- 程序目录会生成 `ECJTU-Net.ini`（配置）和 `ECJTU-Net.log`（日志），已在 .gitignore 中排除。
- 请注意，本软件仅供学习研究使用，请合理使用，不得用于非法用途。本软件作者不会对您可能承担的任何风险和您使用本软件造成的任何影响负任何责任。认为此程序有风险的，请谨慎安装。
