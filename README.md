# Power Menu Guard

Utility C++ kecil untuk `Enable` / `Disable` policy Windows yang menyembunyikan perintah `Shut Down`, `Restart`, `Sleep`, dan `Hibernate` di UI untuk user Windows saat ini.

Scope program ini sengaja terbatas:

- Mengubah policy resmi Windows pada `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\NoClose`
- Bisa diaktifkan dari GUI atau command line
- GUI punya dua mode target:
  - `Admin / current user`
  - `Standard user` lain yang dipilih dari akun lokal
- Source project dan hasil build ada di `C:\Users\Admin\Documents\ANTI shtdw`
- Installer default menaruh aplikasi ke `%LOCALAPPDATA%\Power Menu Guard`
- Build release sekarang pakai `MSVC x64` dengan runtime statik `/MT`
- Uninstaller mengembalikan policy ke kondisi normal

Program ini tidak:

- Memblokir semua software yang punya hak admin untuk melakukan shutdown
- Mengubah perilaku tombol power fisik
- Menjanjikan anti-shutdown mutlak
- Menjamin kompatibel ke semua versi Windows atau Windows 32-bit

Catatan mode `Standard user`:

- Jalankan dari akun admin bila ingin menerapkan policy ke user lain
- Jika profil user target belum ada, login sekali dulu dengan user tersebut
- Bila app tidak dijalankan elevated, tombol apply untuk target user lain akan meminta UAC

## Build

Jalankan:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Prasyarat build:

- Visual Studio Build Tools 2022 dengan komponen `MSVC x64/x86 build tools`
- NSIS bila ingin membentuk installer

Output:

- `dist\PowerMenuGuard.exe`
- `dist\Power Menu Guard Setup.exe`
- `dist\Power Menu Guard Portable x64.zip`
- `dist\Enable Power Menu Guard.cmd`
- `dist\Disable Power Menu Guard.cmd`
- `dist\Status Power Menu Guard.cmd`

Paket yang disarankan untuk dipindah ke PC lain:

- `Power Menu Guard Setup.exe` untuk instalasi normal
- `Power Menu Guard Portable x64.zip` untuk copy manual

Build release MSVC ini tidak lagi butuh DLL runtime tambahan di samping `PowerMenuGuard.exe`.

## Command line

Switch ini cocok untuk automation atau installer. Dari PowerShell, panggil dengan `Start-Process -Wait` supaya proses GUI ditunggu sampai selesai.

```powershell
Start-Process -FilePath .\dist\PowerMenuGuard.exe -ArgumentList "--status" -Wait
Start-Process -FilePath .\dist\PowerMenuGuard.exe -ArgumentList "--enable" -Wait
Start-Process -FilePath .\dist\PowerMenuGuard.exe -ArgumentList "--disable" -Wait
Start-Process -FilePath .\dist\PowerMenuGuard.exe -ArgumentList "--enable --user namauser" -Verb RunAs -Wait
```
