# InternetLimiter

InternetLimiter is a local Windows NetLimiter-style application for per-application network traffic monitoring, setting download/upload bandwidth limits, and blocking internet access.

## Requirements
- **OS:** Windows 10 / 11 (x64)
- **Runtime / SDK:** .NET 8 Runtime
- **Privileges:** Administrator rights to run the Windows Service (required to load the WinDivert driver)
- **WinDivert:** Kernel driver (loaded automatically when the service starts)

## Status and Origin
This project is a trimmed MVP fork of the open-source project [OpenNetLimit](https://github.com/SysAdminDoc/OpenNetLimit) (MIT).

**Features removed in this MVP:**
- REST API / remote management
- Plugin webhooks
- GeoIP lookup
- VirusTotal verification of executables
- Quotas and automatic auto-throttle
- Bandwidth notifications and alerts
- Scheduling
- Theming and localization

## Build and Test
```powershell
dotnet restore OpenNetLimit.sln
dotnet build OpenNetLimit.sln
dotnet test OpenNetLimit.sln
```

## Running
1. **Service – Requires admin rights:**
   Open PowerShell as Administrator and run:
   ```powershell
   dotnet run --project src/OpenNetLimit.Service
   ```
2. **User interface (UI):**
   ```powershell
   dotnet run --project src/OpenNetLimit.UI
   ```

## Usage
- The UI main window shows a list of running processes and their current transfer speeds.
- Right-click a process to open a menu for **setting a limit** (in KB/s) or **removing a limit**.
- Data and rules are stored in `%ProgramData%\OpenNetLimit\`:
  - `rules.json` – saved rules
  - `traffic.db` – SQLite traffic statistics database
  - `logs/` – service application logs

## Warnings
- **Administrator rights:** The service must run with admin privileges to communicate with the WinDivert driver.
- **Antivirus / EDR:** The WinDivert driver is a freely available dual-use driver that may be reported as potentially unwanted by some antivirus programs or EDR.
- **Performance and accuracy:** The default limiting accuracy is approximate (depends on the token bucket size). At speeds above 1 Gbps, higher CPU usage may occur.

## License
- **Application code:** MIT License (see [LICENSE](LICENSE))
- **WinDivert:** LGPL-3.0 (see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt))

---

Created by [Jirkosaur](https://github.com/Jirkosaur) with assistance from Gemini.
