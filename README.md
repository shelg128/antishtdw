# anti shtdw utilities

Repo ini sekarang berisi dua utility Windows yang berbeda:

1. `Power Menu Guard`
   Utility C++ untuk `Enable` / `Disable` policy Windows yang menyembunyikan perintah `Shut Down`, `Restart`, `Sleep`, dan `Hibernate` di UI per-user.

2. `QEMU Guest Agent Guard`
   Utility desktop .NET untuk melihat status service `QEMU-GA`, lalu `Enable` atau `Disable` lagi dengan satu klik.

## Power Menu Guard

Scope utility ini sengaja terbatas:

- Mengubah policy resmi Windows pada `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\NoClose`
- Bisa diaktifkan dari GUI atau command line
- GUI punya dua mode target:
  - `Admin / current user`
  - `Standard user` lain yang dipilih dari akun lokal
- Installer default menaruh aplikasi ke `%LOCALAPPDATA%\Power Menu Guard`
- Build release pakai `MSVC x64` dengan runtime statik `/MT`
- Uninstaller mengembalikan policy ke kondisi normal

Utility ini tidak:

- Memblokir semua software yang punya hak admin untuk melakukan shutdown
- Mengubah perilaku tombol power fisik
- Menjanjikan anti-shutdown mutlak
- Menjamin kompatibel ke semua versi Windows atau Windows 32-bit

Catatan mode `Standard user`:

- Jalankan dari akun admin bila ingin menerapkan policy ke user lain
- Jika profil user target belum ada, login sekali dulu dengan user tersebut
- Bila app tidak dijalankan elevated, tombol apply untuk target user lain akan meminta UAC

### Build Power Menu Guard

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Prasyarat:

- Visual Studio Build Tools 2022 dengan komponen `MSVC x64/x86 build tools`
- NSIS bila ingin membentuk installer

Output:

- `dist\PowerMenuGuard.exe`
- `dist\Power Menu Guard Setup.exe`
- `dist\Power Menu Guard Portable x64.zip`
- `dist\Enable Power Menu Guard.cmd`
- `dist\Disable Power Menu Guard.cmd`
- `dist\Status Power Menu Guard.cmd`

## QEMU Guest Agent Guard

Utility ini ditujukan untuk mesin Windows guest yang memakai `QEMU Guest Agent`.

Fungsinya:

- membaca status service `QEMU-GA`
- menampilkan startup mode dan path service
- `Enable`: set startup ke `Automatic` lalu start service
- `Disable`: stop service lalu set startup ke `Disabled`
- jika belum admin, app akan meminta UAC

Batasnya:

- hanya mengontrol service `QEMU-GA` di dalam Windows guest
- tidak bisa menahan `hard power off` dari host atau provider VM

### Build QEMU Guest Agent Guard

```powershell
powershell -ExecutionPolicy Bypass -File .\QemuGaGuard\build.ps1
```

Prasyarat:

- .NET SDK 8

Output:

- `QemuGaGuard\dist\publish\QemuGaGuard.exe`
- `QemuGaGuard\dist\QemuGaGuard-portable-win-x64.zip`

Probe headless:

```powershell
.\QemuGaGuard\dist\publish\QemuGaGuard.exe --export-state .\QemuGaGuard\dist\state.json
```
