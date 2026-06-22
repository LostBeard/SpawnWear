@echo off
REM ============================================================================
REM Recover a watch whose DEPLOYED APP region is corrupt (CLR hangs at boot, the
REM runtime stops replying to nf-deploy). Full erase_flash (clears the bad app +
REM everything) then reflash the firmware. Use when a plain reflash isn't enough
REM because the corrupt managed deployment survives in the deploy partition.
REM
REM Prereq: watch in bootloader/DOWNLOAD mode (hold BOOT, power-cycle PWR, release
REM   BOOT) -> COM6. After this, PMIC power-cycle -> clean runtime on COM3 (empty
REM   deploy region), then redeploy the managed app fresh.
REM
REM Usage: tools\nf-recover-py313.bat COM6
REM ============================================================================
setlocal
if "%~1"=="" ( echo Usage: nf-recover-py313.bat COM6 & exit /b 1 )
set COM=%~1
set "PATH=C:\Python313;C:\Python313\Scripts;%PATH%"
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1
set IDF_TOOLS_PATH=C:\Espressif

call "C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat" > "%TEMP%\nf-recover-export.log" 2>&1
if errorlevel 1 ( echo [ERROR] ESP-IDF export.bat failed - see %TEMP%\nf-recover-export.log & exit /b 1 )

set NF_BUILD=D:\users\tj\Projects\nf-interpreter\nf-interpreter\build
if not exist "%NF_BUILD%\nanoCLR.bin" ( echo [ERROR] %NF_BUILD%\nanoCLR.bin missing & exit /b 2 )

echo ==^> ERASING entire flash on %COM% ^(clears the corrupt deployed app^)
esptool --chip esp32s3 --port %COM% --baud 921600 erase_flash
if errorlevel 1 ( echo [ERROR] erase_flash failed & exit /b 3 )

echo.
echo ==^> Reflashing firmware ^(bootloader@0x0 + partition@0x8000 + nanoCLR@0x10000^)
esptool --chip esp32s3 --port %COM% --baud 921600 ^
    --before default_reset --after hard_reset write_flash ^
    --flash_mode dio --flash_size detect --flash_freq 80m ^
    0x0     "%NF_BUILD%\bootloader\bootloader.bin" ^
    0x8000  "%NF_BUILD%\partition_table\partition-table.bin" ^
    0x10000 "%NF_BUILD%\nanoCLR.bin"
if errorlevel 1 ( echo [ERROR] write_flash failed & exit /b 4 )

echo.
echo ==^> Recovery complete. PMIC power-cycle ^(hold PWR ^> 6s, then click^) -^> clean
echo     runtime on COM3 with an EMPTY deploy region. Then redeploy the app.
endlocal
