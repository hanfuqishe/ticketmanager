@echo off
setlocal
rem 工单邮件管理器 安装脚本（由 IExpress 自解压包解压后运行）
set "DEST=%LOCALAPPDATA%\TicketManager"
set "ZIP=%~dp0TicketManager-1.0.2-Portable.zip"
echo 正在安装到 %DEST% ...
if exist "%DEST%" rd /s /q "%DEST%"
md "%DEST%" 2>nul
powershell -NoProfile -Command "Expand-Archive -Path '%ZIP%' -DestinationPath '%DEST%' -Force"
if errorlevel 1 (
  echo 安装失败：无法解压程序文件。
  pause
  exit /b 1
)
rem 创建桌面快捷方式
powershell -NoProfile -Command "$ws=New-Object -ComObject WScript.Shell; $d=[Environment]::GetFolderPath('Desktop'); $s=$ws.CreateShortcut($d+'\工单邮件管理器.lnk'); $s.TargetPath=$env:LOCALAPPDATA+'\TicketManager\TicketManager.exe'; $s.WorkingDirectory=$env:LOCALAPPDATA+'\TicketManager'; $s.Save()"
rem 启动应用
start "" "%DEST%\TicketManager.exe"
echo.
echo 安装完成！
