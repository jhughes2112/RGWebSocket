@echo off
REM Builds a throwaway Docker image, runs the ChatTest stress harness inside it (on Linux), then deletes the image.
REM Any arguments are passed through to ChatTest, e.g.:  run_tests_docker.bat clients=64 seed=999
REM Exit code is 0 on PASS, nonzero on FAIL, so this can gate anything that cares.
setlocal
set IMAGE=rgwebsocket-chattest:throwaway

docker build -f Dockerfile.chattest -t %IMAGE% .
if errorlevel 1 (
	echo DOCKER BUILD FAILED
	exit /b 1
)

docker run --rm %IMAGE% %*
set RESULT=%ERRORLEVEL%

docker rmi %IMAGE% >nul 2>nul

if %RESULT% NEQ 0 (
	echo TESTS FAILED with exit code %RESULT%
	exit /b %RESULT%
)
echo TESTS PASSED
exit /b 0
