@call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build"\vcvars64.bat
SET here=%~dp0
pushd "%here%"
rmdir /s /q publish 2>nul
dotnet clean RGWebSocket.sln --nologo -c Release
dotnet restore RGWebSocket.sln --nologo
REM Each output folder is a complete drop-in: copy its contents next to your project and reference the DLLs.
dotnet build RGWebSocket\RGWebSocket.csproj --no-incremental --nologo -c Release -o publish\net10
dotnet build RGWebSocketUnity\RGWebSocketUnity.csproj --no-incremental --nologo -c Release -o publish\unity
pause
