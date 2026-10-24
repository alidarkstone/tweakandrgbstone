@echo off
rem rgbstone MSI renk testi: anakarta bagli fanlari birkac saniye yesil, kirmizi, mavi yapar, sonra eski haline dondurur.
set "exe=%~dp0rgbstone-yeni.exe"
if not exist "%exe%" set "exe=%~dp0rgbstone.exe"
if not exist "%exe%" (
  echo rgbstone-yeni.exe bu dosyayla ayni klasorde olmali.
  pause
  exit /b
)
start "" "%exe%" --msi-test
