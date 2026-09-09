@echo off
rem 一键构建安装包: 1) dotnet publish 自包含单文件  2) Inno Setup 编译
rem 产物: installer\Output\WindowSwitcherSetup-<version>.exe
setlocal
set ROOT=%~dp0..

set ISCC="%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist %ISCC% set ISCC="%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not exist %ISCC% (
    echo ISCC.exe not found - install Inno Setup 6 first ^(winget install JRSoftware.InnoSetup^)
    exit /b 1
)

echo === dotnet publish (self-contained single-file) ===
dotnet publish "%ROOT%\src\WindowSwitcherWpf" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish"
if errorlevel 1 exit /b 1

echo === Inno Setup compile ===
%ISCC% "%~dp0WindowSwitcher.iss"
if errorlevel 1 exit /b 1

echo === DONE: see %~dp0Output (version = .iss MyAppVersion) ===
endlocal
