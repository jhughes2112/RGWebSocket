@call "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build"\vcvars64.bat
SET here=%~dp0
pushd "%here%"
erase /q publish\*.*
dotnet clean RGWebSocket.sln --nologo -c Release
dotnet restore RGWebSocket.sln --nologo
dotnet build RGWebSocket.sln --no-incremental --nologo -c Release -o publish
pause