@echo off
echo ===== 窗口切换器 卸载 =====
echo 这将: 关闭应用 - 删除配置 - 删除程序文件夹
choice /c YN /m "确认卸载? (Y=是, N=否)"
if errorlevel 2 goto :cancel

taskkill /IM WindowSwitcherWpf.exe /F >nul 2>&1
rem 等进程真正退出(最多约10秒), 否则 dll/exe 还被锁定删不掉
set /a waits=0
:waitkill
tasklist /FI "IMAGENAME eq WindowSwitcherWpf.exe" 2>nul | %SystemRoot%\System32\find.exe /I "WindowSwitcherWpf.exe" >nul
if not errorlevel 1 (
    ping -n 2 127.0.0.1 >nul
    set /a waits+=1
    if %waits% geq 20 goto :killdone
    goto :waitkill
)
:killdone
rd /s /q "%APPDATA%\WindowSwitcherWpf" 2>nul
echo 正在删除程序文件夹...
(goto) 2>nul & rmdir /s /q "%~dp0.."
exit

:cancel
echo 已取消卸载。
ping -n 3 127.0.0.1 >nul
