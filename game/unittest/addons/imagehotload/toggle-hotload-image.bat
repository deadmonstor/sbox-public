@echo off
setlocal

set "ASSET_DIR=%~dp0Assets\textures"
set "LIVE_IMAGE=%ASSET_DIR%\hotload.png"
set "BEFORE_IMAGE=%ASSET_DIR%\hotload-before.png"
set "AFTER_IMAGE=%ASSET_DIR%\hotload-after.png"

if not exist "%LIVE_IMAGE%" (
	echo Missing image: "%LIVE_IMAGE%"
	exit /b 1
)
if not exist "%BEFORE_IMAGE%" (
	echo Missing image: "%BEFORE_IMAGE%"
	exit /b 1
)
if not exist "%AFTER_IMAGE%" (
	echo Missing image: "%AFTER_IMAGE%"
	exit /b 1
)

fc /b "%LIVE_IMAGE%" "%BEFORE_IMAGE%" >nul 2>&1
if errorlevel 1 (
	copy /y "%BEFORE_IMAGE%" "%LIVE_IMAGE%" >nul
	if errorlevel 1 exit /b 1
	echo Switched to BEFORE image.
) else (
	copy /y "%AFTER_IMAGE%" "%LIVE_IMAGE%" >nul
	if errorlevel 1 exit /b 1
	echo Switched to AFTER image.
)

endlocal
