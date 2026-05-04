@echo on
setlocal
set MSYSTEM=
set MSYS=
set PYTHONNOUSERSITE=1
set IDF_TOOLS_PATH=C:\Espressif

call "C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat" > "%TEMP%\nf-export.log" 2>&1
if errorlevel 1 exit /b 1

cd /d D:\users\tj\Projects\nf-interpreter\nf-interpreter
echo === cmake configure (forces re-pickup of CMakeLists.txt edits) ===
cmake --preset ESP32_S3_BLE_QSPI > "D:\temp\nfconfigure.log" 2>&1
if errorlevel 1 (
    echo configure failed - log at D:\temp\nfconfigure.log
    exit /b 2
)
echo === cmake build ===
cmake --build --preset ESP32_S3_BLE_QSPI > "D:\temp\nfbuild.log" 2>&1
set BUILD_RC=%ERRORLEVEL%
echo === build returned %BUILD_RC% ===
exit /b %BUILD_RC%
