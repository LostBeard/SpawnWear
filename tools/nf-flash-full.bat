@echo off
REM Full flash of nf-interpreter (bootloader + partition table + nanoCLR) via esptool.
REM
REM Usage: nf-flash-full.bat COM10
REM   COM10 (or whatever the bootloader-mode port is) is REQUIRED.
REM
REM Prereq: watch must be in bootloader mode (hold BOOT, tap RESET, release BOOT).
REM         Confirm with `nanoff --listports` - the bootloader port is the one that
REM         appears alongside an erase-able config rather than the runtime CDC.
REM
REM Output: writes bootloader@0x0, partition-table@0x8000, nanoCLR@0x10000.
REM         Flash params from flasher_args.json: dio, detect, 80m, esp32s3.

setlocal

if "%~1"=="" (
    echo Usage: nf-flash-full.bat COM10
    exit /b 1
)
set COM=%~1

set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1
set IDF_TOOLS_PATH=C:\Espressif

call "C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat" > "%TEMP%\nf-flash-full-export.log" 2>&1
if errorlevel 1 (
    echo [ERROR] ESP-IDF export.bat failed - see %TEMP%\nf-flash-full-export.log
    exit /b 1
)

set NF_BUILD=D:\users\tj\Projects\nf-interpreter\nf-interpreter\build

if not exist "%NF_BUILD%\nanoCLR.bin" (
    echo [ERROR] %NF_BUILD%\nanoCLR.bin missing - run nf-build.bat first
    exit /b 2
)
if not exist "%NF_BUILD%\bootloader\bootloader.bin" (
    echo [ERROR] %NF_BUILD%\bootloader\bootloader.bin missing
    exit /b 2
)
if not exist "%NF_BUILD%\partition_table\partition-table.bin" (
    echo [ERROR] %NF_BUILD%\partition_table\partition-table.bin missing
    exit /b 2
)

echo ==^> Flashing custom nf-interpreter to %COM% ^(esp32s3, dio, 80m^)
echo     bootloader@0x0
echo     partition-table@0x8000
echo     nanoCLR@0x10000
echo.

esptool --chip esp32s3 --port %COM% --baud 921600 ^
    --before default_reset --after hard_reset write_flash ^
    --flash_mode dio --flash_size detect --flash_freq 80m ^
    0x0     "%NF_BUILD%\bootloader\bootloader.bin" ^
    0x8000  "%NF_BUILD%\partition_table\partition-table.bin" ^
    0x10000 "%NF_BUILD%\nanoCLR.bin"

if errorlevel 1 (
    echo [ERROR] esptool flash failed
    exit /b 3
)

echo.
echo ==^> Flash complete. PMIC power-cycle the watch ^(hold PWR ^> 6s, then click^)
echo     and the runtime should come up on COM9.
endlocal
