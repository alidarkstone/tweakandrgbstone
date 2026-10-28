# tweakandrgbstone

Tweakstone (GPU ayar aracı) ve rgbstone (RGB kontrol programı) tek programda. Portable, tek exe, yönetici izniyle çalışır.

Pencerenin üstündeki **GPU / RGB** düğmesiyle iki bölüm arasında geçilir. Tek tepsi simgesi, tek "Windows ile başlat" görevi ve tek ayar klasörü vardır. RGB efekti, GPU sekmesi açıkken de çalışmaya devam eder.

## Çalıştırma

- `tweakandrgbstone.exe`'yi istediğin klasöre koy ve çalıştır. Yönetici izni ister (ekran kartı ayarı yazmak ve RAM ışıkları için).
- **Ekran kartı:** NVIDIA (NVML) ve AMD Radeon (ADLX, RX 6000/9000 hedefli, henüz AMD kartta denenmedi). Ryzen dahili GPU varsa asıl kart seçilir.
- Ayarlar, profiller ve kayıtlar exe'nin yanındaki `tweakandrgbstoneData` klasöründe tutulur. Klasör yazılamazsa `%LOCALAPPDATA%\tweakandrgbstone` kullanılır.
- **İlk açılışta eski ayarlar alınır** (eski klasörler sadece okunur, değiştirilmez):
  - GPU: exe'nin yanındaki `GpuTweakData` klasörü, yoksa `%LOCALAPPDATA%\GpuTweak`
  - RGB: exe'nin yanındaki `rgbstone.json`, yoksa `%APPDATA%\rgbstone\rgbstone.json`
  - Yarıda kalmış oturum ve otomatik ayar dosyaları (`session.lock`, `autotune.json`) alınmaz.
- Eski Tweakstone veya rgbstone açıksa program açılmaz ve önce onları kapatmanı ister; ikisi aynı anda ekran kartını ve LED'leri kontrol etmeye çalışırdı.
- Eski programlar hâlâ Windows ile başlıyorsa bir kez sorar; "Evet" dersen sadece onların başlangıç görevleri silinir.
- **Windows ile başlat**: GPU sekmesindeki Profillerim kartında. Görev Zamanlayıcı'ya "tweakandrgbstone" görevi eklenir.
- Tepsi simgesine sağ tıklayınca **RGB efekti** alt menüsünden efekt değiştirilebilir.

Komut satırı:

| Komut | Ne yapar |
|---|---|
| `--demo` | NVIDIA kartı olmayan bilgisayarda GPU arayüzünü dener |
| `--scan` | RGB cihazlarını tarar, 4 sn efekt gönderir, sonucu `tweakandrgbstoneData\rgbstone.log`'a yazar |
| `--msi-test` | Sürücüsü olmayan MSI anakartlar için kısa renk testi |

GPU tarafının ayrıntıları (güvenlik korumaları, otomatik ayar, doğrulama testi, oyun kararlılığı) Tweakstone ile aynıdır: [docs/tweakstone-README.md](docs/tweakstone-README.md). RGB tarafının desteklenen cihazları ve sürücü ekleme rehberi: [docs/rgbstone/README.md](docs/rgbstone/README.md).

## Yapı

```
src/GpuTweak.Core     GPU: NVML ve ADLX, otomatik ayar, güvenlik, profiller (Tweakstone v0.13.0'dan, değişmedi)
src/RgbStone.Core     RGB: efekt motoru, sürücüler, HID/NVAPI/SMBus, web panosu (rgbstone'dan, .NET 8'e taşındı)
src/GpuTweak.App      Tek pencere, tepsi, başlangıç görevi; RGB sekmesi Rgb/ klasöründe
tests/                GPU çekirdeği ve eski ayarları içeri alma testleri
```

## Derleme

```powershell
dotnet publish src\GpuTweak.App -c Release -o publish
```

.NET 8 SDK gerekir. Çıktı `publish\tweakandrgbstone.exe` (self-contained, ek kurulum istemez).

RGB eklentileri (`plugins\*.dll`) artık `RgbStone.Core.dll`'e karşı .NET 8 ile derlenmeli; rgbstone için derlenmiş eski eklentiler yüklenmez.
