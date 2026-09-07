@echo off
echo 正在关闭 窗口切换器 (Window Switcher)...
taskkill /IM WindowSwitcherWpf.exe /F >nul 2>&1
if %errorlevel%==0 (
    echo 已关闭。
) else (
    echo 窗口切换器当前没有在运行。
)
ping -n 3 127.0.0.1 >nul
