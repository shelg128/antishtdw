# QEMU Guest Agent Guard

Utility desktop Windows untuk toggle service `QEMU-GA` dengan dua aksi utama:

- `Enable`: set startup ke `Automatic` lalu start service
- `Disable`: stop service lalu set startup ke `Disabled`

## Build

```powershell
Set-Location "C:\Users\Admin\Documents\ANTI shtdw\QemuGaGuard"
.\build.ps1
```

Hasil publish:

- `dist\publish\QemuGaGuard.exe`
- `dist\QemuGaGuard-portable-win-x64.zip`

## Catatan

- Aksi enable/disable butuh hak administrator.
- Tool ini hanya mengontrol service guest `QEMU-GA`.
- Kalau provider mematikan VM langsung dari host, guest Windows tetap tidak bisa menahan itu.
- Verifikasi headless:

```powershell
.\dist\publish\QemuGaGuard.exe --export-state .\dist\state.json
```
