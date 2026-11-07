@echo off
chcp 65001 >nul
echo.
echo  Création de la version à mettre en ligne...
echo.

cd /d "%~dp0"
if exist publish rmdir /s /q publish

rem WinUI 3 se compile avec le MSBuild de Visual Studio (dotnet publish ne suffit pas).
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if not defined MSBUILD (
  echo  Visual Studio 2022 est introuvable : installe-le d'abord (voir README).
  pause
  exit /b 1
)

"%MSBUILD%" "Lucie\Lucie.csproj" /restore /t:Publish /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishDir="%~dp0publish\Lucie\\" /v:minimal
if errorlevel 1 (
  echo.
  echo  La compilation a échoué : regarde les erreurs ci-dessus.
  pause
  exit /b 1
)

rem Fichiers d'interface (.pri, .xbf) que la publication oublie parfois
xcopy /e /y /i /q "Lucie\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\*" "publish\Lucie\" >nul

rem On n'embarque pas le modèle vocal : Lucie le télécharge toute seule au premier lancement.
if exist "publish\Lucie\VoiceModel\vosk-model-fr" rmdir /s /q "publish\Lucie\VoiceModel\vosk-model-fr"

powershell -NoProfile -Command "Compress-Archive -Path 'publish\Lucie' -DestinationPath 'publish\Lucie-Windows-x64.zip' -Force"

echo.
echo  Terminé ! Le fichier à mettre en ligne est :
echo    %~dp0publish\Lucie-Windows-x64.zip
echo.
explorer "publish"
pause
