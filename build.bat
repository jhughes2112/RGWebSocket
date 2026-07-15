@call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build"\vcvars64.bat
SET here=%~dp0
pushd "%here%"
rmdir /s /q build 2>nul
dotnet restore RGWebSocket.sln --nologo

REM Each project is a standalone drop-in. Server and Client land in their own folders so you can copy
REM one folder wholesale. Every DLL ships with a portable .pdb that has the source embedded, so you can
REM step straight into the library source from the consuming project.

REM Server package: RGWebSocket.dll + .pdb (net10, Core+Server, AOT-clean). Drop into a server project.
dotnet build RGWebSocket.csproj --no-incremental --nologo -c Release -o build\Server

REM Client package: RGWebSocketUnity.dll + .pdb (+ System.Threading.Channels.dll). Drop into Unity Assets\Plugins.
dotnet build RGWebSocketUnity.csproj --no-incremental --nologo -c Release -o build\Client

pause
