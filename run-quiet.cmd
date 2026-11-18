@echo off
rem Starts BirdCam without noisy HEVC decoder warnings.
rem OpenCV reads these variables when the native library loads,
rem so they must be set before the process starts.
setlocal
set "OPENCV_FFMPEG_LOGLEVEL=8"
set "OPENCV_FFMPEG_CAPTURE_OPTIONS=rtsp_transport;tcp"

set "EXE=%~dp0BirdCam.UI\bin\Debug\net10.0-windows\BirdCam.UI.exe"
if not exist "%EXE%" set "EXE=%~dp0BirdCam.UI\bin\Release\net10.0-windows\BirdCam.UI.exe"
if not exist "%EXE%" (
    echo BirdCam.UI.exe not found. Build the solution first:
    echo     dotnet build BirdCam.slnx
    exit /b 1
)

start "" "%EXE%" %*