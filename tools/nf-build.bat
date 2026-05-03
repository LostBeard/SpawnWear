@echo off
REM SpawnWear nf-interpreter rebuild helper.
REM
REM Usage: nf-build.bat [preset]
REM   preset defaults to ESP32_S3_BLE_QSPI (the SpawnWear watch target)
REM
REM Activates ESP-IDF v5.5.4 (the version pinned by current nf-interpreter main)
REM and runs cmake configure + build against the requested preset.
REM
REM Output: nf-interpreter\build\nanoCLR.bin (firmware blob)
REM         nf-interpreter\build\bootloader\bootloader.bin
REM         nf-interpreter\build\partitions_*.bin

setlocal

REM Block MSYS bash check in export.bat
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1

REM ESP-IDF v5.5.4 uses Python 3.13 venv (idf5.5_py3.13_env). Do NOT prepend 3.11.
REM The system PATH should expose Python 3.13 naturally; export.bat picks the venv
REM matching the installed python.exe version.
set IDF_TOOLS_PATH=C:\Espressif

REM Activate ESP-IDF v5.5.4 environment (verbose to console for diagnostics)
call "C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat"
if errorlevel 1 (
    echo [ERROR] ESP-IDF export.bat failed
    exit /b 1
)

set NF_DIR=D:\users\tj\Projects\nf-interpreter\nf-interpreter
cd /d "%NF_DIR%"

set PRESET=%1
if "%PRESET%"=="" set PRESET=ESP32_S3_BLE_QSPI

echo ==^> Configuring %PRESET%
cmake --preset %PRESET%
if errorlevel 1 (
    echo [ERROR] cmake configure failed
    exit /b 2
)

echo ==^> Building %PRESET%
cmake --build --preset %PRESET%
if errorlevel 1 (
    echo [ERROR] cmake build failed
    exit /b 3
)

echo.
echo ==^> Build complete. Firmware artifacts:
dir "%NF_DIR%\build\nanoCLR.bin" "%NF_DIR%\build\bootloader\bootloader.bin" "%NF_DIR%\build\partitions_4mb.bin" 2>nul

endlocal
