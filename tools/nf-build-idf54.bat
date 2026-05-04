@echo on
REM Forced ESP-IDF v5.4.1 build of the nf-interpreter firmware. Used 2026-05-05
REM to bisect the deploy ceiling - testing whether v5.4.1 vs v5.5.4 explains
REM the corruption gone in the v5.5.4 rebuild.

setlocal
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1
set IDF_TOOLS_PATH=C:\Espressif

REM Prepend Python 3.11.2 to PATH so v5.4.1's export.bat finds the
REM matching idf5.4_py3.11_env venv (NOT the 3.13 venv that v5.5.4 uses).
set PATH=C:\Espressif\tools\idf-python\3.11.2;%PATH%

call "C:\Espressif\frameworks\esp-idf-v5.4.1\export.bat" > "%TEMP%\nf-export-54.log" 2>&1
if errorlevel 1 (
    echo export.bat v5.4.1 failed - log at %TEMP%\nf-export-54.log
    exit /b 1
)

cd /d D:\users\tj\Projects\nf-interpreter\nf-interpreter

REM Force a fresh CMake configure to pick up the new ESP_IDF_PATH.
echo === wiping build dir for clean v5.4.1 configure ===
if exist build rmdir /S /Q build

echo === cmake configure (v5.4.1) ===
cmake --preset ESP32_S3_BLE_QSPI > "D:\temp\nfconfigure-54.log" 2>&1
if errorlevel 1 (
    echo configure failed - log at D:\temp\nfconfigure-54.log
    exit /b 2
)

echo === cmake build ===
cmake --build --preset ESP32_S3_BLE_QSPI > "D:\temp\nfbuild-54.log" 2>&1
set BUILD_RC=%ERRORLEVEL%
echo === build returned %BUILD_RC% ===
exit /b %BUILD_RC%
