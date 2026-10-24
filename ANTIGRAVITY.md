# ANTIGRAVITY.md: Architecture & Design Guidelines for WinIsland

### Konteks Proyek
**WinIsland** adalah aplikasi *overlay* interaktif berbentuk kapsul di bagian tengah atas layar Windows 11 yang mengemulasi dan memperluas fungsi Dynamic Island.

### 4 Pilar Desain & Standar Kualitas (Wajib Dipatuhi Agen)

1. **Estetika (*Aesthetic Design*):**
   * **Visual & Warna:** Warna dasar kapsul `#0A0A0C` (*Deep Matte Black*) dengan *border* halus `#1F1F24` ketebalan $1\text{ px}$ dan sudut membulat penuh (*pill-shaped*, *corner radius* dinamis mengikuti tinggi kapsul).
   * **Animasi Fisika Pegas (*Spring Physics*):** Seluruh transisi perubahan dimensi dari mode *Compact* ($180 \times 34\text{ px}$) ke mode *Expanded* ($420 \times 150\text{ px}$) wajib menggunakan persamaan osilator harmonik teredam dengan target kecepatan bingkai $\ge 60\text{ fps}$:
     $$F_{\text{spring}} = -k \cdot (x - x_{\text{target}}) - c \cdot v$$
     dengan konstanta kekakuan $k = 320$, koefisien redaman $c = 26$, dan massa $m = 1.0$.
   * **Mikro-Interaksi:** Menyediakan gelombang *audio visualizer* yang responsif, efek pendaran cahaya (*charging glow*) saat daya terhubung, dan karakter interaktif *Pixel Pet* di atas kapsul.

2. **Pilar *Useful* (Fungsionalitas Produktivitas Nyata):**
   * **File Shelf / Drop Zone:** Mendukung OLE *Drag-and-Drop* untuk menampung sementara *path* file lintas jendela tanpa perlu menekan `Alt + Tab` berulang kali.
   * **Media Controller (`SMTC`):** Terintegrasi dengan `Windows.Media.Control` untuk membaca metadata dan mengendalikan Spotify, Apple Music, atau *tab* YouTube di browser.
   * **HUD Volume & Kecerahan:** Mengambil alih tampilan indikator volume (`CoreAudio`) dan kecerahan layar ke dalam kapsul.
   * **Quick Actions & Hardware Monitor:** Menyediakan *Pomodoro Timer*, *Clipboard History* ($10$ item terakhir), pratinjau cermin *webcam*, serta telemetri penggunaan CPU, RAM, suhu GPU, dan baterai.

3. **Pilar *Usable* (Ergonomi & Pencegahan Salah Klik):**
   * **Hover Trigger Delay:** Wajib menerapkan jeda waktu $250\text{ ms}$ (`DispatcherTimer` / `CancellationTokenSource`) sebelum kapsul membesar saat disentuh kursor agar tidak menghalangi klik pada *tab* browser (Chrome/Edge) atau *title bar* IDE.
   * **Click-Through Area:** Area transparan di luar bentuk kapsul hitam harus meneruskan klik ke aplikasi di bawahnya menggunakan *window style* `WS_EX_TRANSPARENT` dan `WS_EX_LAYERED` melalui Win32 API.
   * **Fullscreen Auto-Hide:** Kapsul otomatis bersembunyi saat mendeteksi aplikasi layar penuh (*game*, pemutar video, atau presentasi).
   * **Modifier Key Override:** Mendukung opsi membuka kapsul hanya ketika tombol `Alt` ditahan saat *hover*.

4. **Pilar *Used* (Performa & Keberlanjutan Penggunaan):**
   * **Jejak Memori & CPU Ultra-Ringan:** Penggunaan RAM saat *idle* wajib berada di bawah $< 45\text{ MB}$ dan penggunaan CPU saat *idle* $< 0.5\%$.
   * **Arsitektur Modular:** Setiap modul (*Media*, *File Shelf*, *Telemetry*, *Pomodoro*, *Pixel Pet*) dapat dinyalakan atau dimatikan melalui file konfigurasi `settings.json`.
