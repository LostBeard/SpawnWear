@echo off
REM ============================================================================
REM Flash the custom nf-interpreter runtime with the Python-3.13 fix (2026-06-19).
REM Wraps nf-flash-full.bat (esptool: bootloader@0x0 + partition@0x8000 +
REM nanoCLR@0x10000) with the Python-3.13 PATH so esptool/export resolve.
REM
REM Prereq: watch in bootloader/DOWNLOAD mode (hold BOOT, power-cycle via PWR,
REM   release BOOT) -> enumerates as COM6 ("USB JTAG/serial debug unit").
REM   After flashing, PMIC power-cycle (hold PWR >6s, single-click) -> runtime on COM3.
REM
REM Usage: tools\nf-flash-py313.bat COM6
REM Run from the PowerShell tool, NOT Git Bash.
REM ============================================================================
setlocal
if "%~1"=="" ( echo Usage: nf-flash-py313.bat COM6 & exit /b 1 )
set "PATH=C:\Python313;C:\Python313\Scripts;%PATH%"
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1
set IDF_TOOLS_PATH=C:\Espressif
call "%~dp0nf-flash-full.bat" %~1
endlocal
