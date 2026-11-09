@echo off
rem Menu console GHOST CLONAGE WINDOWS (WinPE). Les operations destructives redemandent une confirmation.
rem Note : pas de blocs ( ... ) autour de "set /p" : les variables y seraient developpees trop tot.
chcp 65001 >nul
set "G=X:\GhostClonage\GhostClonageCli.exe"
if not exist "%G%" set "G=%~dp0GhostClonageCli.exe"

:menu
cls
echo ==========================================================
echo   GHOST CLONAGE WINDOWS  -  support de demarrage (WinPE)
echo ==========================================================
echo.
echo   1. Lister les disques
echo   2. CLONER un disque vers un autre
echo   3. CREER UNE IMAGE d'un disque
echo   4. RESTAURER une image sur un disque
echo   5. VERIFIER une image
echo   6. CREER UN POINT DE RESTAURATION (disque entier)
echo   7. REVENIR A UN POINT DE RESTAURATION
echo   8. Ouvrir une invite de commandes
echo   9. Redemarrer
echo.
set "CHOIX="
set /p CHOIX="Votre choix : "
if "%CHOIX%"=="1" goto do_list
if "%CHOIX%"=="2" goto do_clone
if "%CHOIX%"=="3" goto do_image
if "%CHOIX%"=="4" goto do_restore
if "%CHOIX%"=="5" goto do_verify
if "%CHOIX%"=="6" goto do_pointcreate
if "%CHOIX%"=="7" goto do_pointrevert
if "%CHOIX%"=="8" goto do_shell
if "%CHOIX%"=="9" goto do_reboot
goto menu

:do_list
"%G%" list
pause
goto menu

:do_clone
"%G%" list
set "S="
set "D="
set /p S="Numero du disque SOURCE : "
set /p D="Numero du disque DESTINATION : "
"%G%" clone --src %S% --dst %D%
pause
goto menu

:do_image
"%G%" list
set "S="
set "F="
set /p S="Numero du disque SOURCE : "
set /p F="Fichier image de sortie (ex. D:\sauvegarde.gci - jamais sur le disque source) : "
"%G%" image --src %S% --out "%F%"
pause
goto menu

:do_restore
set "F="
set "D="
set /p F="Fichier image a restaurer (ex. D:\sauvegarde.gci) : "
"%G%" list
set /p D="Numero du disque DESTINATION : "
"%G%" restore --image "%F%" --dst %D%
pause
goto menu

:do_verify
set "F="
set /p F="Fichier image a verifier : "
"%G%" verify --image "%F%"
pause
goto menu

:do_pointcreate
"%G%" list
set "S="
set "P="
set "L="
set /p S="Numero du disque ENTIER a proteger : "
set /p P="Dossier des points (sur un AUTRE disque, ex. D:\Points) : "
set /p L="Nom du point : "
"%G%" point-create --src %S% --dir "%P%" --label "%L%"
pause
goto menu

:do_pointrevert
set "P="
set "F="
set "D="
set /p P="Dossier des points (ex. D:\Points) : "
"%G%" points --dir "%P%"
set /p F="Chemin complet du fichier .gci du point a utiliser : "
"%G%" list
set /p D="Numero du disque a ramener a ce point : "
"%G%" point-revert --file "%F%" --dst %D%
pause
goto menu

:do_shell
cmd /k
goto menu

:do_reboot
wpeutil reboot
goto menu
