@echo off
setlocal
pushd "%~dp0.."
roslyn-codelens-mcp ".\TwinCatGateway.sln"
set "roslyn_exit_code=%ERRORLEVEL%"
popd
exit /b %roslyn_exit_code%
