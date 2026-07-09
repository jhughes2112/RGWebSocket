@call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build"\vcvars64.bat
SET here=%~dp0
pushd "%here%"
rmdir /s /q build 2>nul
dotnet restore RGWebSocket.sln --nologo
REM Final DLLs land flat in build\ (net10 RGWebSocket.dll, ns2.1 RGWebSocketUnity.dll + System.Threading.Channels.dll for Unity).
dotnet build RGWebSocket.csproj --no-incremental --nologo -c Release -o build
dotnet build RGWebSocketUnity.csproj --no-incremental --nologo -c Release -o build
pause
