# rgbstone

![rgbstone](assets/logo-banner.png)

Taşınabilir, internetsiz, tek exe RGB kontrol programı. Tüm cihazlar aynı saatten beslenen aynı efektle senkron çalışır.

## Kullanım
- `dist\rgbstone.exe` dosyasını istediğin klasöre kopyala ve çalıştır (yönetici izni ister).
- Ayarlar exe'nin yanındaki `rgbstone.json`, kayıtlar `rgbstone.log` dosyasındadır. Exe'nin klasörü yazılamıyorsa `%APPDATA%\rgbstone` kullanılır.
- İlk açılışta SignalRGB'de ayarladığın fan/şerit LED sayıları (sadece okunarak) içeri aktarılır.
- "Windows ile başlat" kutusu, Görev Zamanlayıcı'da en yüksek yetkiyle çalışan bir oturum açma görevi oluşturur (UAC sorusu çıkmaz).
- Pencereyi kapatınca tepside çalışmaya devam eder. Çıkış tepsi menüsündedir.
- `rgbstone.exe --scan` cihazları tarar, 4 saniye efekt gönderir ve sonucu günlüğe yazar.
- Hiçbir ağ bağlantısı yoktur: telemetri, güncelleme veya uzaktan kod yok.

## Desteklenen cihazlar
Bu bilgisayarda denenenler:

| Sürücü | Cihazlar | Bağlantı |
|---|---|---|
| MSI Mystic Light (761 protokol) | X870E Carbon vb. anakartlar, 3 ARGB başlık + EZ konnektör + kart üstü LED'ler | USB HID |
| NZXT HUE 2 | Control Hub, Smart Device V2, RGB & Fan Controller; takılı fanları hub'dan okur (F120/F420 RGB Core vb.) | USB HID |
| Logitech HID++ | PRO X TKL (tuş başına), 8070/8071 özellikli G fareler; alıcının tüm kanalları taranır | USB HID |
| SteelSeries QcK Prism | Medium / XL / 3XL / 5XL | USB HID |
| PNY / Palit / Gainward GPU | RTX 30/40/50 serisi (I2C 0x49) | NVIDIA NVAPI |
| Corsair DDR5 (varsayılan kapalı) | Vengeance RGB DDR5, Dominator Titanium | SMBus (inpoutx64) |
| HID LampArray | Windows Dinamik Aydınlatma standardını destekleyen her cihaz | USB HID |

Deneysel (protokol hazır, gerçek donanımda henüz denenmedi):

| Sürücü | Cihazlar |
|---|---|
| ASUS Aura | Aura USB anakart denetleyicileri (0B05:18F3/19AF/1939/1BED/1AA6) |
| Corsair | Lighting Node Pro/Core, Commander Pro, LS100, LT100, Spec Omega |
| Razer Chroma | 1532 kimlikli tüm modern cihazlar, tek renk olarak |
| Gigabyte RGB Fusion 2 | ITE 048D:5702 / 8297 anakartlar |
| ASRock Polychrome | 26CE:01A2 anakartlar, 8 bölge (12V, ARGB, PCH, I/O, PCB) |

Bilerek eklenmeyenler: Lian Li UNI FAN hub'ları. Eski model ham USB sürücüsü istiyor; yeni modellerde ışık komutları hub'ın fan hızı ayarlarıyla iç içe. Fan hızına dokunmadan güvenle sürmek için gerçek donanımda denemek gerekiyor. O zamana kadar "Tanınmayan cihazlar" listesinde görünürler.

Tanınmayan ama RGB markası olan cihazlar Ayarlar > Tanınmayan cihazlar altında kimlikleriyle listelenir.

## Diğer özellikler
- **Tak-çalıştır:** USB cihaz takılınca/çıkarılınca 3 saniye içinde yeniden tarar.
- **Üretici yazılımına saygı:** rgbstone açıldıktan sonra bir üretici yazılımı açılırsa o markanın cihazı ona bırakılır, yazılım kapanınca kontrol geri alınır.
- **Servis devralma (isteğe bağlı):** MSI Mystic Light servisleri rgbstone açıkken durdurulur, kapanınca geri başlatılır.
- **Web panosu (isteğe bağlı):** ev ağından, PIN ile; kapalıyken hiçbir port açılmaz.

### RAM uyarısı
DDR5'te RAM ışıkları bellek voltaj yongalarıyla aynı SMBus hattındadır. rgbstone:
- RAM kontrolünü varsayılan olarak kapalı tutar,
- SignalRGB, iCUE, HWiNFO, MSI LightKeeper gibi SMBus kullanan programlar açıkken RAM'e hiç yazmaz,
- yalnızca 0x18-0x1B adreslerine yazar, başka adres kodda engellidir,
- her yazmadan önce adres kaydını tekrar okuyup başka bir program değiştirdiyse yazmayı iptal eder.

## Yeni marka eklemek (eklenti mimarisi)
Her sürücü `IDeviceProvider` arayüzünü uygular. `Detect()` cihazları bulur ve `RgbDevice` nesneleri döndürür. Cihaz da `Initialize()` (doğrudan kontrol moduna geçiş) ve `Apply()` (renkleri gönderme) yazar.

```csharp
public sealed class MyBrandProvider : IDeviceProvider
{
    public string Name => "MyBrand";
    public IEnumerable<RgbDevice> Detect(DetectContext ctx)
    {
        foreach (var info in ctx.HidDevices.Where(d => d.VendorId == 0x1234 && d.ProductId == 0x5678))
            yield return new MyBrandDevice(HidDevice.Open(info));
    }
}
```

İki yol var:
1. `src/rgbstone/Drivers/` içine yeni bir dosya ekle ve yeniden derle (tek exe kalır).
2. Ayrı bir DLL olarak derleyip exe'nin yanındaki `plugins\` klasörüne koy; rgbstone açılışta otomatik yükler.

Hazır taşıma katmanları: `HidDevice` (çıktı/özellik raporları, zaman aşımlı okuma), `NvApi` (NVIDIA I2C), `Piix4Smbus` (sistem SMBus).

## Derleme
```powershell
.\build.ps1
```
Betik gerekirse .NET 8 SDK'yı `.tools\` klasörüne kurar ve `dist\rgbstone.exe` üretir. Çıktı .NET Framework 4.8.1'e (Windows 10/11'de hazır gelir) derlenir, ek bir çalışma zamanı gerekmez.

Logo: `tools\make-logo.ps1` (`assets\` klasörüne ico ve png yazar).
