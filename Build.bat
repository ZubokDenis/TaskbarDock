@echo off
setlocal
echo Building TaskbarDock...

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Error: csc.exe not found at %CSC%
    exit /b 1
)

"%CSC%" /nologo /target:winexe /win32icon:app.ico /lib:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF /r:System.dll,System.Drawing.dll,System.Windows.Forms.dll,UIAutomationClient.dll,UIAutomationTypes.dll,WindowsBase.dll /out:TaskbarDock.exe NativeMethods.cs Config.cs Logger.cs DockOverlayWindow.cs DockEngine.cs SettingsForm.cs Program.cs

if %ERRORLEVEL% equ 0 (
    echo.
    echo Build SUCCESS: TaskbarDock.exe
) else (
    echo.
    echo Build FAILED with code %ERRORLEVEL%!
    exit /b %ERRORLEVEL%
)
