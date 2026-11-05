@echo off
chcp 65001 >nul
cd /d "%~dp0.."

:menu
echo.
echo  1  BOOTH用の .unitypackage を作る(dist フォルダが開きます)
echo  2  変更をコミットして push
echo  3  タグを付けて push(リリース)
echo  4  終了
echo.
set /p n=番号:
if "%n%"=="1" goto build
if "%n%"=="2" goto commit
if "%n%"=="3" goto tag
if "%n%"=="4" exit /b
goto menu

:build
python scripts\make_unitypackage.py
if errorlevel 1 (echo 失敗しました & goto menu)
start "" "%cd%\dist"
goto menu

:commit
git status --short
echo.
set /p msg=コミットメッセージ(空なら中止):
if "%msg%"=="" goto menu
git add -A
git commit -m "%msg%"
git push
goto menu

:tag
echo 現在のバージョン:
findstr /c:"\"version\"" Packages\com.nyaohtech.*\package.json
type scripts\unitypackage.json
echo.
echo タグの形:
echo   VCC用    com.nyaohtech.simplematresize-1.1.4
echo   BOOTH用  unitypackage-1.0.0
set /p tg=タグ名(空なら中止):
if "%tg%"=="" goto menu
git tag %tg%
git push origin %tg%
goto menu
