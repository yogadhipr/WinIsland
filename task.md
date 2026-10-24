# Task List: Pengembangan WinIsland (Dynamic Island PC Windows)

## Fase 1: Core Engine & Usable Foundation (MVP)
- [x] Inisialisasi solusi `WinIsland.sln` dengan proyek `WinIsland.App` (.NET 8 WPF) dan `WinIsland.Tests` (xUnit).
- [x] Implementasi `Win32Interop.cs` untuk jendela *Borderless*, *Always-on-Top*, `WS_EX_TOOLWINDOW`, dan `WS_EX_NOACTIVATE`.
- [x] Implementasi `SpringAnimator.cs` dengan kurva pegas ($k = 320, c = 26$) yang berjalan pada `CompositionTarget.Rendering`.
- [x] Implementasi `HoverIntentController.cs` dengan *Trigger Delay* $250\text{ ms}$ dan opsi pintasan tombol `Alt` + *Hover*.
- [x] Implementasi deteksi aplikasi/game *Fullscreen* (`SHQueryUserNotificationState`) untuk fitur *Auto-Hide*.
- [x] Buat unit test di `WinIsland.Tests` untuk `SpringAnimator` dan `HoverIntentController`, lalu verifikasi dengan `dotnet test`.

## Fase 2: Useful Productivity Modules (Media, HUD, & File Shelf)
- [x] Tambahkan referensi paket NuGet untuk Windows Media / WinRT dan `NAudio` (untuk CoreAudio HUD & FFT Visualizer).
- [x] Implementasi `MediaControlService.cs` untuk membaca judul lagu, artis, sampul album, dan kontrol *Play/Pause/Next* dari `SMTC`.
- [x] Bangun `MediaWidget.xaml` lengkap dengan animasi *audio visualizer* dinamis.
- [x] Implementasi `FileShelfService.cs` dan `FileDropZone.xaml` yang otomatis membuka kapsul saat file diseret (*Drag-Over Auto-Expand*) dan mengizinkan file ditarik keluar kembali (*Drag-Out*).
- [x] Implementasi pengganti HUD Volume dengan dukungan *Scroll Wheel* di atas kapsul untuk mengubah volume sistem secara langsung.

## Fase 3: System Monitoring & Quick Tools
- [x] Implementasi `TelemetryService.cs` untuk memantau persentase CPU, penggunaan RAM, dan status baterai/pengisian daya dengan interval pembaruan hemat daya ($2000\text{ ms}$).
- [x] Implementasi `ClipboardService.cs` untuk menyimpan $10$ teks/gambar terakhir yang disalin pengguna.
- [x] Bangun `QuickToolsView.xaml` yang memuat *Pomodoro Timer* interaktif dan daftar klik-cepat *Clipboard History*.

## Fase 4: Aesthetic Polish, Retention (Used) & Verifikasi Akhir
- [x] Tambahkan efek pendaran cahaya halus (*Ambient Charging Glow*) saat status daya laptop mendeteksi pengisian baterai.
- [x] Bangun modul `PixelPetCanvas.xaml` yang menampilkan karakter piksel interaktif di bagian atas kapsul saat mode *Compact*.
- [x] Implementasi `SettingsManager.cs` (`settings.json`) dan ikon *System Tray* untuk mengatur modul aktif, panjang jeda *hover*, dan *Run on Startup*.
- [x] Lakukan *profiling* penggunaan RAM dan CPU saat *idle*, lalu hasilkan dokumen laporan akhir di `walkthrough.md`.
