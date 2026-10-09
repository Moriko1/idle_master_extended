@echo off
setlocal
cd /d "%~dp0.."
where python >nul 2>nul
if errorlevel 1 (
  echo Python 3 is required to run the build script.
  exit /b 1
)
python scripts\build.py %*
exit /b %errorlevel%
