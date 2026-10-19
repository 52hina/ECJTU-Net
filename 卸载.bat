@echo off
title 卸载 校园网自动登录
echo.
echo ==============================================
echo   卸载 校园网自动登录工具
echo ==============================================
echo.
echo   将做以下操作：
echo     1. 结束后台正在运行的 ECJTU-Net.exe（含掉线守护）
echo     2. 删除开机自启快捷方式
echo     3. 删除程序、图标、配置、日志、临时文件
echo     4. 删除整个文件夹（含本卸载脚本）
echo.
echo   按任意键开始卸载；要取消请直接关闭本窗口。
pause >nul

echo.
echo   [1/4] 结束后台程序...
taskkill /f /im ECJTU-Net.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
taskkill /f /im ECJTU-Net.exe >nul 2>&1

echo   [2/4] 删除开机自启...
del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\ECJTU-Net.lnk" >nul 2>&1

echo   [3/4] 删除程序文件...
cd /d "%~dp0"
del /f /q "ECJTU-Net.exe" >nul 2>&1
del /f /q "ECJTU-Net.ico" >nul 2>&1
del /f /q "ECJTU-Net.ini" >nul 2>&1
del /f /q "ECJTU-Net.ini.tmp" >nul 2>&1
del /f /q "ECJTU-Net.log" >nul 2>&1
del /f /q "ECJTU-Net.log.1" >nul 2>&1
del /f /q "*.tmp" >nul 2>&1

echo   [4/4] 删除整个文件夹...
echo.
echo   卸载完成。按任意键结束并清理本文件夹...
pause >nul

cd /d "%TEMP%"
(goto) 2>nul & rmdir /s /q "%~dp0"