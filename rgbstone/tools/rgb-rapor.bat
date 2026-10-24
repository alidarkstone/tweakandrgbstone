@echo off
rem rgbstone donanim raporu: sadece okur, hicbir yere bir sey gondermez, donanima yazmaz.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c = Get-Content -LiteralPath '%~f0' -Encoding UTF8; $i = [array]::IndexOf($c, ':PS'); Invoke-Expression ($c[($i + 1)..($c.Count - 1)] -join [Environment]::NewLine)"
pause
exit /b
:PS
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'rgb-rapor.txt'
$r = New-Object System.Collections.Generic.List[string]
function Add($s) { $r.Add([string]$s) }
function Section($s) { Add ''; Add "=== $s ===" }

Add "rgbstone donanim raporu - $(Get-Date -Format 'yyyy-MM-dd HH:mm')"

Section 'Anakart'
Get-CimInstance Win32_BaseBoard | ForEach-Object { Add "$($_.Manufacturer) | $($_.Product) | $($_.Version)" }
Get-CimInstance Win32_BIOS | ForEach-Object { Add "BIOS: $($_.SMBIOSBIOSVersion) ($($_.ReleaseDate))" }

Section 'Ekran karti'
Get-CimInstance Win32_VideoController | ForEach-Object { Add "$($_.Name) | $($_.PNPDeviceID)" }

Section 'RAM'
Get-CimInstance Win32_PhysicalMemory | ForEach-Object { Add "$($_.Manufacturer) | $($_.PartNumber.Trim()) | $([math]::Round($_.Capacity / 1GB)) GB" }

Section 'USB ve HID cihazlari'
Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -match '^(USB|HID)\\VID_' } |
    Sort-Object PNPDeviceID | ForEach-Object { Add "$($_.Name) | $($_.Manufacturer) | $($_.PNPDeviceID)" }

Section 'USB urun adlari (cihazin kendi bildirdigi)'
Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '^USB\\VID_' } | ForEach-Object {
    $desc = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_BusReportedDeviceDesc' -ErrorAction SilentlyContinue).Data
    if ($desc) { Add "$desc | $($_.InstanceId)" }
}

Section 'Klavye ve fare'
Get-CimInstance Win32_Keyboard | ForEach-Object { Add "Klavye: $($_.Name) | $($_.PNPDeviceID)" }
Get-CimInstance Win32_PointingDevice | ForEach-Object { Add "Fare: $($_.Name) | $($_.Manufacturer) | $($_.PNPDeviceID)" }

Section 'Kurulu RGB / donanim programlari'
$keys = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'RGB|Mystic|MSI|iCUE|Corsair|NZXT|Razer|Logi|SteelSeries|Aura|Armoury|Fusion|Polychrome|Lian|Thermaltake|Cooler|HyperX|Redragon|Rampage|Gamdias|Xigmatek|Signal|OpenRGB|PNY|XLR8|Velocity' } |
    Sort-Object DisplayName -Unique | ForEach-Object { Add "$($_.DisplayName) | $($_.Publisher)" }

$r | Out-File -FilePath $out -Encoding UTF8
Write-Host ''
Write-Host "Rapor hazir: $out"
Write-Host 'Bu dosyayi gonderen kisiye ilet. Pencereyi kapatabilirsin.'
