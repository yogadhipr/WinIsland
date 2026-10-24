# Rencana Implementasi Teknis: WinIsland (Dynamic Island PC Windows)

### Tujuan Arsitektur
Membangun aplikasi desktop Windows berbasis **C# (.NET 8.0 - Windows)** menggunakan **WPF (Windows Presentation Foundation)** dan integrasi **WinRT / Win32 API (`user32.dll`, `shell32.dll`)**.

### Struktur Direktori yang Akan Dibangun
```text
WinIsland/
├── ANTIGRAVITY.md
├── implementation_plan.md
├── task.md
├── WinIsland.sln
├── src/
│   └── WinIsland.App/
│       ├── WinIsland.App.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── Core/
│       │   ├── Win32Interop.cs          # P/Invoke user32.dll (Always-on-top, Click-through, Fullscreen check)
│       │   ├── SpringAnimator.cs        # Engine animasi fisika pegas 60fps
│       │   ├── HoverIntentController.cs # Logika Trigger Delay 250ms & Modifier Key (Alt)
│       │   └── SettingsManager.cs       # Konfigurasi modular JSON
│       ├── Services/
│       │   ├── MediaControlService.cs   # Integrasi Windows.Media.Control (SMTC)
│       │   ├── AudioHudService.cs       # CoreAudio API untuk volume & visualizerFFT
│       │   ├── FileShelfService.cs      # Penampung OLE Drag-and-Drop sementara
│       │   ├── TelemetryService.cs      # Pemantau CPU, RAM, & Baterai
│       │   └── ClipboardService.cs      # Listener riwayat clipboard Windows
│       ├── Views/
│       │   ├── IslandWindow.xaml        # Jendela overlay utama tanpa bingkai
│       │   ├── Modules/
│       │   │   ├── MediaWidget.xaml     # Tampilan pemutar musik & visualizer
│       │   │   ├── FileDropZone.xaml    # Laci transit drag-and-drop file
│       │   │   ├── QuickToolsView.xaml  # Pomodoro timer & Clipboard history
│       │   │   └── PixelPetCanvas.xaml  # Sprite animasi Pixel Pet di atas kapsul
│       └── ViewModels/
│           └── MainIslandViewModel.cs   # State machine: Compact, Hovered, Expanded, Hidden
└── tests/
    └── WinIsland.Tests/
        ├── WinIsland.Tests.csproj
        ├── SpringAnimatorTests.cs       # Unit test konvergensi fisika pegas
        └── HoverIntentTests.cs          # Unit test pembatalan hover < 250ms
```

### Spesifikasi Komponen Kunci

#### A. `Core/Win32Interop.cs`
* Mengatur jendela dengan `WindowStyle="None"`, `AllowsTransparency="True"`, dan `Topmost="True"`.
* Menggunakan `GetWindowLong` dan `SetWindowLong` dengan konstanta:
  * `WS_EX_TOOLWINDOW = 0x00000080` (menyembunyikan ikon dari *Alt+Tab* switcher).
  * `WS_EX_NOACTIVATE = 0x08000000` (mencegah jendela mencuri fokus *keyboard* dari IDE/aplikasi aktif saat diklik).
* Menggunakan `SHQueryUserNotificationState` dari `shell32.dll` untuk mendeteksi status `QUNS_BUSY` atau `QUNS_RUNNING_D3D_FULL_SCREEN` guna mengaktifkan *Auto-Hide* secara otomatis.
* Hit-testing click-through via `WM_NCHITTEST` atau transparent window handling agar area di luar kapsul dapat di-click-through ke aplikasi di belakangnya.

#### B. `Core/SpringAnimator.cs`
* Model matematika osilator harmonik teredam:
  $$F_{\text{spring}} = -k \cdot (x - x_{\text{target}}) - c \cdot v$$
  $$a = F_{\text{spring}} / m$$
* Menggunakan integrasi numerik (Euler atau Verlet/RK4) per frame pada `CompositionTarget.Rendering`.
* Target frame rate $\ge 60\text{ fps}$ dengan pembaruan dinamis `Width`, `Height`, dan `CornerRadius`.

#### C. `Core/HoverIntentController.cs`
* Saat `MouseEnter` terpicu, mulai hitung mundur selama $\Delta t = 250\text{ ms}$ menggunakan `CancellationTokenSource` / `DispatcherTimer`.
* Jika `MouseLeave` terjadi pada $t < 250\text{ ms}$, batalkan token sehingga kapsul tetap berada pada status *Compact* (melindungi *tab* browser dari pembesaran tidak sengaja).
* Saat pengguna menyeret file (`DragEnter` dengan `DataFormats.FileDrop`), lewati jeda waktu ($\Delta t = 0\text{ ms}$) dan langsung buka laci *File Drop Zone*.
* Mendukung mode tombol penahan (Modifier Key `Alt` + *Hover*).

#### D. `Services/MediaControlService.cs` & `AudioHudService.cs`
* Menggunakan SDK `Microsoft.Windows.SDK.Contracts` (atau `net8.0-windows10.0.19041.0`) untuk mengakses `GlobalSystemMediaTransportControlsSessionManager`.
* Membaca thumbnail, judul lagu, artis, serta kontrol *Play/Pause/Next/Prev*.
* `AudioHudService` mengintegrasikan `NAudio` untuk CoreAudio master volume control & audio visualizer data.

#### E. `Services/FileShelfService.cs` & `TelemetryService.cs` & `ClipboardService.cs`
* `FileShelfService` menampung item drop file dengan representasi visual ikon dan path, siap ditarik keluar dengan `DragDrop.DoDragDrop()`.
* `TelemetryService` membaca persentase CPU, RAM, status baterai pengisian daya hemat energi.
* `ClipboardService` memantau riwayat teks dan gambar terbaru via Win32 Clipboard Viewer listener (`AddClipboardFormatListener`).

### Rencana Verifikasi (*Verification Plan*)
1. **Pengujian Unit Otomatis (`dotnet test`):**
   * Memastikan `SpringAnimator` mencapai nilai target $x_{\text{target}}$ dengan toleransi kesalahan $\epsilon < 0.01$ dalam waktu $< 400\text{ ms}$.
   * Memastikan `HoverIntentController` tidak memicu *state* `Expanded` jika durasi kursor berada di atas kapsul hanya $150\text{ ms}$.
2. **Verifikasi Build & Eksekusi:**
   * Solusi berhasil di-compile tanpa error (`dotnet build`).
   * Seluruh unit tests lulus (`dotnet test`).
   * Profiling memori & verifikasi fitur visual & UX.
