@echo off
title RAMMap System Tray Companion

:: Auto-elevate with administrator permissions if needed
net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

set "EXE_PATH=%~dp0RAMMapTray.exe"
if not exist "%EXE_PATH%" set "EXE_PATH=%~dp0..\publish\RAMMapTray.exe"
if not exist "%EXE_PATH%" set "EXE_PATH=%~dp0..\src\bin\Release\net10.0-windows\win-x64\publish\RAMMapTray.exe"
if not exist "%EXE_PATH%" set "EXE_PATH=%~dp0..\src\bin\Release\net8.0-windows\win-x64\publish\RAMMapTray.exe"

if exist "%EXE_PATH%" (
    start "" "%EXE_PATH%"
) else (
    echo [ERROR] No se encontro RAMMapTray.exe.
    pause
)
