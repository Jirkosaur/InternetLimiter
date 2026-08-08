# InternetLimiter

InternetLimiter je lokální Windows aplikace typu NetLimiter pro sledování síťového provozu per aplikace, nastavování download/upload limitů a blokování přístupu k internetu.

## Požadavky
- **Operační systém:** Windows 10 / 11 (x64)
- **Runtime / SDK:** .NET 8 Runtime
- **Oprávnění:** Administrátorská práva pro spuštění Windows Service (vyžadováno pro nahrání ovladače WinDivert)
- **WinDivert:** Kernel driver (automaticky nahrán při spuštění služby)

## Stav a Původ
Tento projekt je osekaným MVP forkem open-source projektu [OpenNetLimit](https://github.com/SysAdminDoc/OpenNetLimit) (MIT).

**Osekané (odstraněné) funkce v tomto MVP:**
- REST API / Vzdálená správa
- Plugin Webhooky
- GeoIP lookup
- VirusTotal verifikace spustitelných souborů
- Kvóty a automatický auto-throttle
- Bandwidth notifikace a alerty
- Časové plány (Scheduling)
- Tematické přepínání (Theming) a lokalizace

## Build a Testování
```powershell
dotnet restore OpenNetLimit.sln
dotnet build OpenNetLimit.sln
dotnet test OpenNetLimit.sln
```

## Spuštění
1. **Služba (Service) – Vyžaduje Admin práva:**
   Otevřete PowerShell jako Administrátor a spusťte:
   ```powershell
   dotnet run --project src/OpenNetLimit.Service
   ```
2. **Uživatelské rozhraní (UI):**
   ```powershell
   dotnet run --project src/OpenNetLimit.UI
   ```

## Použití
- V hlavním okně UI vidíte přehled běžících procesů a jejich aktuální přenosovou rychlost.
- Kliknutím pravým tlačítkem na proces otevřete nabídku pro **nastavení limitu** (v KB/s) nebo **odebrání limitu**.
- Data a pravidla jsou uložena v adresáři `%ProgramData%\OpenNetLimit\`:
  - `rules.json` – uložená pravidla
  - `traffic.db` – SQLite databáze statistik provozu
  - `logs/` – aplikační logy služby

## Upozornění
- **Administrátorská práva:** Služba vyžaduje spuštění s admin právy pro komunikaci s ovladačem WinDivert.
- **Antivirus / EDR:** Driver WinDivert je volně dostupný dual-use ovladač, který může být některými antivirovými programy či EDR hlášen jako potenciálně nežádoucí.
- **Výkon a přesnost:** Výchozí přesnost limitování je přibližná (v závislosti na velikosti token bucketu). Při rychlostech nad 1 Gbps může dojít k vyššímu vytížení CPU.

## Licence
- **Kód aplikace:** MIT License (viz [LICENSE](file:///C:/Users/jirin/Desktop/InternetLimiter/LICENSE))
- **WinDivert:** LGPL-3.0 (viz [THIRD-PARTY-NOTICES.txt](file:///C:/Users/jirin/Desktop/InternetLimiter/THIRD-PARTY-NOTICES.txt))
