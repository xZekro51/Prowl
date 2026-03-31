@echo off
:: Prowl Documentation Server Launcher
:: Checks for Node.js, installs dependencies if needed, and starts the Docusaurus dev server.

where node >nul 2>&1
if %errorlevel% neq 0 (
    echo [Prowl Docs] Node.js not found in PATH. Skipping documentation server.
    echo [Prowl Docs] Install Node.js from https://nodejs.org to enable local docs.
    exit /b 0
)

cd /d "%~dp0"

if not exist "node_modules" (
    echo [Prowl Docs] Installing dependencies...
    call npm install
    if %errorlevel% neq 0 (
        echo [Prowl Docs] npm install failed. Skipping documentation server.
        exit /b 0
    )
)

echo [Prowl Docs] Starting Docusaurus dev server at http://localhost:3000 ...
call npx docusaurus start
