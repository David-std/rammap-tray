@echo off
title ConfigStartWindows - RAMMap Tray Companion

:: Check and elevate to administrator
net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

set "EXE_PATH=%~dp0RAMMapTray.exe"
if not exist "%EXE_PATH%" set "EXE_PATH=%~dp0..\publish\RAMMapTray.exe"
if not exist "%EXE_PATH%" set "EXE_PATH=%~dp0..\src\bin\Release\net10.0-windows\win-x64\publish\RAMMapTray.exe"
if not exist "%EXE_PATH%" set "EXE_PATH=%~dp0..\src\bin\Release\net8.0-windows\win-x64\publish\RAMMapTray.exe"
if not exist "%EXE_PATH%" (
    echo [ERROR] No se encontro RAMMapTray.exe.
    echo Asegurese de compilar o colocar el ejecutable junto al script o en la carpeta publish.
    pause
    exit /b 1
)

echo ================================================================
echo  Configurando RAMMap Tray Companion para Iniciar con Windows
echo  Ruta detectada: %EXE_PATH%
echo ================================================================
echo.

schtasks /Create /TN "RAMMapTrayCompanion" /TR "'%EXE_PATH%'" /SC ONLOGON /RL HIGHEST /F

reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "RAMMapTrayCompanion" /f >nul 2>&1

echo.
echo ================================================================
echo  Listo! La tarea programada ha sido creada con exito.
echo  RAMMap se iniciara siempre al encender el equipo o iniciar sesion
echo  con maximos privilegios de Administrador y SIN advertencias UAC.
echo ================================================================
echo.
pause
