# SignalRGB İnceleme Raporu (R9, 24.10.2026)

## 1. Kurulum
| Parça | Yer | Not |
|---|---|---|
| Uygulama v2.5.80 | `%LOCALAPPDATA%\VortxEngine\app-2.5.80\Signal-x64\SignalRgb.exe` (118 MB) | Qt6 + tam Chromium (QtWebEngine 206 MB) + Ultralight/WebKit + ffmpeg + Tesseract OCR |
| Eski sürüm | `app-2.5.74` | Squirrel güncelleyici silmemiş |
| Servis | `SignalRgb.Service` → `C:\ProgramData\WhirlwindFX\SignalRgb\service\SignalRgbService.exe` | **LocalSystem**, otomatik başlar, "yükseltilmiş işler" |
| Çekirdek sürücü | `SignalRgbDriver.sys` (otomatik), ayrıca `inpoutx64.sys`, `PawnIO.sys` | SMBus/IO port erişimi (RAM, anakart) |
| Otomatik başlatma | `HKCU\...\Run\SignalRgb` → `SignalRgbLauncher.exe --silent` | |
| Eklentiler | 464 JS cihaz betiği (`Plugins\`) + CDN'den her açılışta 389 dosya + GitHub/GitLab'dan `git fetch` ile addon | Kod her açılışta internetten değişebiliyor |
| Güvenlik duvarı | 14 adet **Inbound Allow / TCP+UDP Any** kuralı (eski sürümlerden kalma 10 tanesi dahil) | |

## 2. Nereye ne gönderiyor (exe içinden çıkarılan adresler + canlı bağlantılar)
- **Canlı:** 3 adet TLS bağlantısı Google'a (Firebase, 142.251.x / 172.217.x).
- **Firebase:** `whirlwindengine.firebaseio.com`, `signalrgbpro.firebaseio.com`, `signalrgbpartners.firebaseio.com`, `identitytoolkit/securetoken.googleapis.com` (oturum, kütüphane, bildirimler), `firebaseremoteconfig` (uzaktan ayar).
- **Analitik / telemetri:** `google-analytics.com/mp/collect` ve `/batch`, `events.statsigapi.net/v1/log_event` (Statsig olay günlüğü + feature-flag), `trackcmp.net/event` ve `whirlwindfx.api-us1.com` (ActiveCampaign pazarlama takibi), `signalrgbmarketplace.pages.dev/tracking/pixelv3?cid&sid&uid` (takip pikseli), `daily_checkin.dat` (günlük check-in).
- **Çökme raporları:** Sentry (`o4505636443652096.ingest.sentry.io`), crashpad_handler.exe.
- **Toplanan veriler:** Kalıcı "User Machine Id" (`{eceeb8ee-…}`), GPU/RAM bilgisi (`system_info.json`), **çalışan tüm işlemlerin listesi** (ConflictMitigator logda tamamını yazıyor), cihaz listesi.
- **Yerel sunucu:** 16034 ve 16038 portları **tüm ağ arayüzlerinde** dinliyor ("SignalApi Active"); güvenlik duvarı kuralı açık olduğu için yerel ağdan erişilebilir. 16036/16037/16039 sadece 127.0.0.1.
- **Uzaktan kod:** her açılışta `gitlab.com/signalrgb/hotfix`, `github.com/SRGBmods/plugins` çekiliyor + plugin CDN. Yani cihazlarınıza ne gönderildiği uzaktan değişebilir.

Kötü amaçlı bir şey görmedim, ama bir RGB programı için gereğinden fazla telemetri, takip ve uzaktan kod indirme var.

## 3. Donanım (SignalRGB'nin bulduğu + USB/PnP)
| Cihaz | Bağlantı | Kimlik |
|---|---|---|
| MSI MPG X870E CARBON WIFI (Mystic Light, 185-byte protokol, per-LED değil) | USB HID | 0DB0:0076 |
| MSI ek cihaz (MI_07 HID) | USB HID | 0DB0:0B58 |
| Gainward RTX 5080 Phoenix (PNY tipi RGB denetleyici) | GPU I2C, adres 0x49 | 10DE:2C02 sub 10B0:F323 |
| Corsair Vengeance RGB DDR5 2×32 GB (CMH64GX5M2B6000Z30, 10 LED) | SMBus PIIX4 0x0B00, adres 0x19 ve 0x1B | |
| NZXT Control Hub | USB HID | 1E71:2022 |
| Logitech PRO X TKL (Lightspeed) | USB HID + Windows Dynamic Lighting (LampArray) | 046D:C547 |
| Logitech Lightspeed alıcı (fare) | USB HID | 046D:C54D |
| SteelSeries QCK Prism Cloth 3XL | USB HID | 1038:1516 |
| SteelSeries ses cihazı (Arctis/GameDAC) | USB HID | 1038:12E0 |
| AMD Ryzen 9 9950X3D, 64 GB RAM | | |

Yüklü üretici yazılımları: MSI Center (MSI_Central_Service, Mystic_Light_Service, LEDKeeper2, LightKeeperService, DCv2), SteelSeries GG (Engine, Prism, Sonar), Logitech G HUB, NZXT CAM, Corsair (servis durmuş), Logitech LampArray servisi.

## 4. Neden yavaş / kararsız olabilir
1. **Ağırlık:** Tek süreç 519 MB RAM, 218 thread; içinde iki ayrı tarayıcı motoru (Chromium + WebKit) çalışıyor. Boştayken bile ~%13 tek çekirdek CPU.
2. **Çakışma:** MSI Center ve SteelSeries GG "whitelist"te olduğu için kapatılmıyor, aynı cihazlara aynı anda yazılıyor. Logda: *"Active conflicts detected, I2C (Ram) Device scan will be unreliable"* ve SMBus mutex **Time Out**.
3. **Açılış:** Her açılışta CDN'den 389 dosya + 2 git deposu çekiliyor, 464 eklenti JS olarak yorumlanıyor, 27 SMBus betiği tek tek taranıyor.
4. **Çökme:** Bugünkü log "SignalRGB has recovered from a crash!" ile başlıyor; son 7 günde 10 log dosyası var.
5. Yönetici olmadan çalışıyor, ama işi LocalSystem servis + çekirdek sürücü üzerinden yapıyor.

## 5. rgbstone için çıkarımlar
- Ağ erişimi **sıfır** (hiçbir telemetri, güncelleme, uzaktan kod yok).
- Eklentiler yerel, imzalı/sabit; JS yorumlayıcı yerine yerel kod sürücüleri.
- Çakışma yönetimi: vendor servislerini kapatmak yerine HID'e özel erişim + SMBus için Windows global mutex (`Global\Access_SMBUS.HTB.Bus`) kullanımı, kullanıcıya hangi süreçlerin çakıştığını gösterme.
- SignalRGB'ye dokunmadım (kaldırmadım, durdurmadım).
