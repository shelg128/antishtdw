# Anti Shutdown Guard

Utility desktop Windows untuk toggle service `QEMU-GA` dan beberapa proteksi guest-side lain dalam satu executable.

- `Enable`: set startup ke `Automatic` lalu start service
- `Disable`: stop service lalu set startup ke `Disabled`
- `AC Power: Do Nothing`: set power button action saat AC ke `Do nothing`
- `Install Keep-Awake`: pasang task `AntiIdleKeepAwake` yang menjalankan executable ini dengan `--keep-awake`
- `Hide Power Menu`: sembunyikan menu Shut down/Restart/Sleep/Hibernate untuk current user

## Build

```powershell
Set-Location "C:\Users\Admin\Documents\ANTI shtdw\QemuGaGuard"
.\build.ps1
```

Hasil publish:

- `dist\publish\QemuGaGuard.exe`
- `dist\QemuGaGuard-portable-win-x64.zip`

## Catatan

- Aksi enable/disable QEMU-GA butuh hak administrator.
- Keep-awake memakai `SetThreadExecutionState`; tidak melakukan autoclick, mouse move, atau input keyboard palsu.
- Tool ini hanya mengontrol bagian yang masih bisa dikendalikan dari dalam Windows guest.
- Kalau provider mematikan VM langsung dari host, guest Windows tetap tidak bisa menahan itu.
- Verifikasi headless:

```powershell
.\dist\publish\QemuGaGuard.exe --export-state .\dist\state.json
```

Mode headless lain:

```powershell
.\dist\publish\QemuGaGuard.exe --keep-awake
.\dist\publish\QemuGaGuard.exe --system-action set-power-button-do-nothing
.\dist\publish\QemuGaGuard.exe --system-action install-keep-awake
```
