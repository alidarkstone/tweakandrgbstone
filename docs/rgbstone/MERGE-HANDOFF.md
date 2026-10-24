# rgbstone → Tweakstone devir raporu (24.10.2026)

Bu dosya, rgbstone'un Tweakstone ile birleştirilen sürümünden sonra rgbstone tarafında yapılan değişiklikleri anlatır. "Project merge" oturumu bunları Tweakstone'daki rgbstone koduna aktarmalıdır.

- Kaynak klasör: `C:\Users\alidarkstone\Documents\rgbstone\src\rgbstone`
- Derleme: `.\build.ps1` → `dist\rgbstone.exe` (.NET Framework 4.8.1, tek exe)
- Güncel sürüm: **0.4.0** (`rgbstone.csproj` içindeki `<Version>`)
- Kod olduğu gibi kopyalanabilir. Tweakstone'da namespace ya da klasör farklıysa sadece onların uyarlanması gerekir.

## 1. Güvenilirlik düzeltmeleri (önemli)

| Konu | Ne değişti | Dosyalar |
|---|---|---|
| MSI kontrolü geri alma | MSI Center arka planda LEDKeeper2'yi (ve ışık servislerini) kendiliğinden yeniden açıyor. Eskiden rgbstone bunu "kullanıcı MSI'ı açtı" sanıp cihazı bırakıyordu. Artık devralma açıkken yardımcıyı tekrar kapatıp cihazları yeniden başlatıyor. MSI Center penceresi açıksa dokunmuyor, kapanınca geri alıyor. 10 dakikada 3'ten fazla geri açılırsa 30 dakika uğraşmıyor. | `Core/Engine.cs` (`ReassertTakeover`, `TakeoverNeeded`), `Core/VendorTakeover.cs` (`Apply` artık durdurduklarını döndürüyor, `TargetsRunning`, `IsTarget`) |
| Devralma sadece gerekince | Kontrol edilen bir MSI cihazı yoksa MSI servisleri durdurulmuyor, tarama sonunda geri açılıyor. | `Core/Engine.cs` |
| RAM / SMBus güvenliği | LEDKeeper2 de Corsair DDR5 RAM'e SMBus üzerinden yazıyor. `SmbusUsers` listesine `LEDKeeper2` ve `Mystic_Light_Service` eklendi. Corsair DDR5'in `ConflictingProcesses` değeri artık `Engine.SmbusUsers`. MSI yardımcısı kapatılmadan önce RAM duraklatılıyor ve 10 saniye sonra yeniden boyanıyor (`RamQuiet`). | `Core/Engine.cs`, `Drivers/CorsairDdr5.cs` |
| Logitech klavye uykudan uyanınca | Klavye uyanınca kendi rengine dönüyordu. Artık yeniden bağlanma bildirimi (HID++ 0x41) yakalanıyor ve kontrol 1 sn ve 4 sn sonra iki kez geri alınıyor. Ayrıca 20 saniyede bir tam kare tekrar gönderiliyor. | `Drivers/LogitechHidpp.cs` |
| Arayüz "Arithmetic overflow" | Cihaz takma/çıkarma kancasında `wParam.ToInt32()` 64 bit değerlerde patlıyordu. Artık sadece `WM_DEVICECHANGE` için `ToInt64` kullanılıyor. | `UI/MainWindow.xaml.cs` |
| Boş listede "Taranıyor…" | Cihaz yoksa tarama bitse de "Taranıyor…" yazıyordu. Artık "Desteklenen cihaz bulunamadı" yazıyor. | `UI/MainWindow.xaml.cs` |
| Takılan sürücü | Her sürücünün taraması 30 saniyede kesiliyor (`DetectTimeout`). Log'a "Tarama bitti: N cihaz" ve tüm HID koleksiyonları (VID:PID/usage page) yazılıyor. | `Core/Engine.cs` |
| İkinci kopya | İkinci kopya sessizce kapanıyordu. Artık "rgbstone zaten çalışıyor" uyarısı veriyor. | `Program.cs` |
| HID rapor boyu | Windows tam rapor boyu istiyor. `Pad` kısa paketleri dolduruyor, uzun paketlerin sıfır kuyruğunu kesiyor. | `Transport/Hid.cs` |

## 2. Yeni ve genişletilen sürücüler (hepsi "deneysel", donanımda denenmedi, sadece MSI 7D77 denendi)

| Sürücü | Kapsam | Dosya |
|---|---|---|
| MSI eski nesil (0x52 protokolü) | OpenRGB'nin güvenli listesindeki 109 anakart (1462:7Bxx–7Exx). 185/162/112 bayt düzen. Başlık başına tek renk: ARGB 1-2, 12V RGB, kart üstü. Flash'a hiç yazmıyor (save bayt 0). **PRO B650M-A (7D77) gerçek kartta denendi, çalıştı.** | `Drivers/MsiMysticLight.cs` (`MsiMysticLightLegacy`, `LegacyLayout`, `LegacyPids`) |
| MSI test modu | `rgbstone.exe --msi-test`: fanları 6'şar saniye yeşil/kırmızı/mavi yapıp eski ayarı geri yazar. | `Program.cs`, `tools/msi-test.bat` |
| ASUS Aura | Eski kartlardaki ayrı ARGB kontrolcüleri (0B05:1867/1872/18A3/18A5) eklendi. | `Drivers/AsusAura.cs` |
| NZXT Kraken X42/X52/X62/X72, M22 | Logo + 8 LED pompa halkası. | `Drivers/NzxtKraken.cs` |
| Lian Li UNI FAN Hub SL, SL V2, AL V2 | 4 kanal, fan başına 16 LED, Lian Li'nin 460 güç sınırı uygulanıyor. | `Drivers/LianLiUniHub.cs` |
| Thermaltake Riing Plus / Quad | 35 PID, 5 port, saniyede bir tekrar gönderim. | `Drivers/ThermaltakeRiing.cs` |
| Corsair Commander Core / XT | Pompa + 6 fan portu, XT'de RGB başlığı. 5 saniyede bir tekrar gönderim, çıkışta donanım moduna dönüş. | `Drivers/CorsairCommanderCore.cs` |
| Corsair iCUE V2 çevre birimleri | 33 model (K55/K57/K60/K70/K95/K100, Dark Core, Harpoon, Ironclaw, Katar, M55/M65/M75, Scimitar, MM700) + 5 Slipstream alıcısı. | `Drivers/CorsairPeripheralV2.cs` |
| HyperX Pulsefire | Surge, FPS Pro, Core, Dart, Raid, Haste (12 kimlik). | `Drivers/HyperXMouse.cs` |
| Wooting | V2 ve V2.12+ yazılımlı tüm Wooting klavyeler (6×21 ızgara, RGB565). | `Drivers/Wooting.cs` |
| SteelSeries Apex / Rival / Sensei | Apex 5/7/9/Pro (1., 2. ve 3. nesil, kablosuz dahil), Rival 100-110/300/310/600/3, Sensei (35 kimlik). | `Drivers/SteelSeriesPeripherals.cs` |
| Ortak yardımcı | HID tuş kodundan klavye üzerindeki yatay konum (efektler klavyede soldan sağa aksın diye). | `Drivers/HidKeyLayout.cs` |

Protokol bilgileri OpenRGB'nin açık kaynak kodundan okundu. Kod rgbstone için baştan yazıldı. SignalRGB bilgisayardan kaldırıldığı için onun betikleri kullanılamadı.

## 3. Yardımcı dosyalar

- `tools/rgb-rapor.bat`: Salt okunur donanım raporu (anakart, GPU, RAM, USB/HID cihazları, kurulu RGB programları) → Masaüstüne `rgb-rapor.txt`. Hiçbir şey göndermez, donanıma yazmaz.
- `tools/msi-test.bat`: Yukarıdaki MSI renk testini başlatır.

## 4. Henüz yapılmayanlar (sonraki gruplar)

Cooler Master ARGB / MP750 / klavyeler, HyperX klavyeler, Glorious (Sinowealth), Roccat, Gigabyte Aorus çevre birimleri, ASUS ROG klavye/fare, Logitech eski G serisi, EVGA/ZOTAC/MSI/ASUS/Gigabyte ekran kartları (I2C yazması, güvenlik kuralı gereği ayrıca onay ister), DeepCool.

## 5. Kurallar (değişmedi)

- RAM ışığı isteğe bağlı (`RamControl`). SMBus kullanan başka program varken RAM'e yazılmaz. Sadece 0x18–0x1B adresleri.
- Ağ erişimi yok (web panosu kapalıyken hiçbir port açılmaz).
