@echo off
REM ============================================================================
REM Build nf-interpreter with the Python-3.13 fix (2026-06-19).
REM
REM WHY: the system Python was upgraded 3.13 -> 3.14, which breaks ESP-IDF's
REM export.bat (it looks for the nonexistent idf5.5_py3.14_env venv) and then
REM cmake's find_package(Python3) grabs system 3.14 (no kconfiglib) -> configure
REM fails. This wrapper forces Python 3.13 on PATH AND pins cmake to the existing
REM idf5.5_py3.13_env venv python.
REM
REM Usage: tools\nf-build-py313.bat [preset]      (default ESP32_S3_BLE_QSPI)
REM Run it from the PowerShell tool, NOT Git Bash (Bash mangles cmd /c -> no-op).
REM
REM NOTE: if you changed a defconfig / sdkconfig.default_* file, delete the cached
REM   D:\users\tj\Projects\nf-interpreter\nf-interpreter\sdkconfig  FIRST, else the
REM   change is ignored (the master sdkconfig wins). Then run this.
REM ============================================================================
setlocal
set "PATH=C:\Python313;C:\Python313\Scripts;%PATH%"
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1
set IDF_TOOLS_PATH=C:\Espressif

call "C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat"
if errorlevel 1 ( echo [ERROR] export.bat failed & exit /b 1 )

set PRESET=%1
if "%PRESET%"=="" set PRESET=ESP32_S3_BLE_QSPI
set NF_DIR=D:\users\tj\Projects\nf-interpreter\nf-interpreter
set VENVPY=C:\Espressif\python_env\idf5.5_py3.13_env\Scripts\python.exe
cd /d "%NF_DIR%"

echo ==^> Configuring %PRESET% (cmake pinned to %VENVPY%)
cmake --preset %PRESET% -DPython3_EXECUTABLE=%VENVPY% -DPython3_FIND_REGISTRY=NEVER -DPython3_FIND_STRATEGY=LOCATION
if errorlevel 1 ( echo [ERROR] cmake configure failed & exit /b 2 )

echo ==^> Building %PRESET%
cmake --build --preset %PRESET%
if errorlevel 1 ( echo [ERROR] cmake build failed & exit /b 3 )

echo ==^> BUILD OK
dir "%NF_DIR%\build\nanoCLR.bin" 2>nul
endlocal
