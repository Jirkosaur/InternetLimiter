# PROGRESS.md

Průběžný stav projektu **InternetLimiter** – lokální aplikace typu NetLimiter (monitoring, limity up/down, blokování), osekaný fork OpenNetLimit.

> **Vykonavatel (Gemini):** tento soubor po každé fázi aktualizuj podle §10 v `EXECUTION_PLAN.md`. Nikdy nemaž dřívější záznamy, pouze přidávej.

---

## Výchozí stav (seed od plánovače)

**Datum:** 2026-08-09

**Cíl projektu:** Jednoduchá, funkční lokální Windows aplikace typu NetLimiter pro osobní použití, publikovaná na GitHub tak, aby ji mohl použít kdokoli z vlastního stroje.

**Klíčová rozhodnutí (závazná):**
- Základ: fork `SysAdminDoc/OpenNetLimit` (MIT), maximálně osekaný na MVP.
- MVP rozsah (PONEHAT): monitoring per aplikace, per-aplikace limity download/upload, blokování, pravidla v JSON, SQLite statistiky, WPF UI + Windows service + named pipe IPC, tray, wildcard pravidla, detekce svchost/UWP.
- ODEJÍT: REST API/remote admin, plugin webhooks, GeoIP, VirusTotal, kvóty, alerty, rule scheduling, theming, lokalizace, (případně connection log).
- Stack: C# / .NET 8, WPF, WinDivert (už podepsaný driver, žádný vlastní kernel driver), SQLite, JSON.
- Role: **plánovač** (uživatel + já) píše `EXECUTION_PLAN.md`; **vykonavatel** (Gemini) provádí pouze kroky z něj a aktualizuje tento soubor.
- Publikace: GitHub repo `InternetLimiter` vytvoří vykonavatel přes `gh` CLI na konci.

**Stroj (ověřeno 2026-08-09):**
- Pracovní adresář: `C:\Users\jirin\Desktop\InternetLimiter` (obsahoval jen EXECUTION_PLAN.md a PROGRESS.md).
- git: `git version 2.46.2.windows.1` ✅
- .NET SDK: **NENÍ nainstalováno** ❌ → vykonavatel se zastaví ve fázi 0 a počká na uživatele (instalace .NET 8 SDK z https://aka.ms/dotnet/download).
- `gh` / `gh auth status`: neověřeno (nutné ověřit ve fázi 0).

**Postup:** viz `EXECUTION_PLAN.md` – §1 pravidla vykonavatele, §3 předpoklady, §4–§9 fáze, §10 šablona zápisu, §11 záchranný protokol, §12 DoD.

---

<!-- Nové záznamy přidávej ZDE pod tento oddělovač. -->

## 2026-08-09 01:40 - Fáze 0: Ověření předpokladů (§3)

**Stav:** ❌ zablokováno

**Provedeno:**
- Spuštěna kontrola prerekvizit podle §3 (`dotnet --version`, `git --version`, `gh --version`, `gh auth status`).

**Build/test:** Neproběhlo (chybí prerekvizity).

**Pozorování / problémy:**
- `dotnet --version` selhalo: "No .NET SDKs were found." (Chybí .NET 8 SDK).
- `git --version` ok: `git version 2.46.2.windows.1`.
- `gh --version` / `gh auth status` selhalo: Příkaz `gh` není nainstalován nebo rozpoznán v PATH.

**Další krok:** Čekám na uživatele: Prosíme o instalaci .NET 8 SDK (https://aka.ms/dotnet/download) a GitHub CLI (`gh`) včetně přihlášení (`gh auth login`). Po dokončení prosím potvrďte pokračování.

