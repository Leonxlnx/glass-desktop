@echo off
rem Builds DockGlass.exe with the C# compiler that ships with Windows.
set GAC=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /target:winexe /out:"%~dp0DockGlass.exe" ^
  /r:"%GAC%\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll" ^
  /r:"%GAC%\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll" ^
  /r:"%GAC%\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll" ^
  "%~dp0DockGlass.cs"
