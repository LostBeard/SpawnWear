@echo off
REM ============================================================================
REM Read + decode the ESP32-S3 coredump from flash over the bootloader port.
REM Watch must be in DOWNLOAD mode (BOOT held + power-cycle) -> COM6.
REM Coredump partition is at 0x8F0000, size 0x10000 (see partitions_nanoclr_16mb.csv).
REM Decodes against the instrumented build's ELF for a symbolicated backtrace.
REM
REM Usage: tools\nf-read-coredump-py313.bat COM6
REM Run from the PowerShell tool, NOT Git Bash.
REM ============================================================================
setlocal
if "%~1"=="" ( echo Usage: nf-read-coredump-py313.bat COM6 & exit /b 1 )
set "PATH=C:\Python313;C:\Python313\Scripts;%PATH%"
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1
set IDF_TOOLS_PATH=C:\Espressif
call "C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat" > "%TEMP%\coredump-export.log" 2>&1
if errorlevel 1 ( echo [ERROR] export.bat failed - see %TEMP%\coredump-export.log & exit /b 1 )

set NF_ELF=D:\users\tj\Projects\nf-interpreter\nf-interpreter\build\nanoCLR.elf
set ESPCD=C:\Espressif\frameworks\esp-idf-v5.5.4\components\espcoredump\espcoredump.py

echo ==^> Reading coredump partition 0x8F0000 (64K) from %~1
esptool --chip esp32s3 --port %~1 --baud 921600 read_flash 0x8F0000 0x10000 "%TEMP%\coredump.bin"
if errorlevel 1 ( echo [ERROR] read_flash failed & exit /b 2 )

echo.
echo ==^> Decoding coredump (info_corefile, ELF format)
python "%ESPCD%" info_corefile -t raw -c "%TEMP%\coredump.bin" "%NF_ELF%"
echo ==^> done (raw partition saved at %TEMP%\coredump.bin)
endlocal
