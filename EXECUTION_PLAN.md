# EXECUTION_PLAN.md

Plán pro vykonavatele (Gemini) – aplikace typu NetLimiter, osekaná verze z OpenNetLimit.

> **Přečti si CELÝ tento dokument od začátku do konce, než začneš.** Pak postupuj po fázích v pořadí §4 → §9. Po každé dokončené fázi aktualizuj `PROGRESS.md` podle §10 a vytvoř git commit. Nic nedělej nad rámec dokumentu.

---

## §1 Pravidla pro vykonavatele (ZÁVAZNÁ)

1. **Jsi výhradně vykonavatel.** Provádíš POUZE kroky popsané v tomto dokumentu, v přesném pořadí. **Neimprovizuješ, nic nepřidáváš, nic nevylepšuješ, nic nerafaktoruješ, nepřidáváš funkce, neměníš design.** Děláš přesně to, co je napsané.
2. **Neptej se na věci, které tento dokument řeší.** Vše je zde rozhodnuto.
3. **Pokud nějaký krok selže:** zapiš do `PROGRESS.md` přesně, co selhalo (příkaz, výstup, chybová hláška), pak postupuj podle §11 (Záchranný protokol). U nevratných/bezpečnostních kroků se **zastav a počkej na uživatele**.
4. **Nevytvářej žádné další soubory** kromě souborů a adresářů, které dokument výslovně požaduje (výjimky: soubory vzniklé buildem, testy, nebo `gh`). Žádné poznámky navíc, žádné README navíc, žádné skripty navíc.
5. **Každý git commit** udělej až po úspěšném dokončení fáze, s commit message podle pokynů fáze. `git commit` neprováděj jindy.
6. **Když je v příkazu `git pull`/`git fetch`/neznámý obsah** – nikdy neprováděj interaktivní merge konflikt sám bez záznamu do `PROGRESS.md`. (Nepředpokládá se, že nastane.)
7. **Nikdy nemaž `EXECUTION_PLAN.md` ani `PROGRESS.md`.** Oba jsou součástí repozitáře.
8. Po každé fázi, která mění kód nebo soubory, **spusť kontrolu**: `dotnet build` (a kde je to psané, i `dotnet test`) musí projít, než jdeš dál. Pokud neprojde, řeš podle §11.

---

## §2 Kontext a cíl projektu

- Stavíme **lokální Windows aplikaci typu NetLimiter**: sledování provozu per aplikace, omezení download/upload rychlosti per aplikace, blokování přístupu k internetu per aplikace.
- **Stack:** C# / .NET 8, WPF (UI), WinDivert (kernel driver, už podepsaný Microsoftem), SQLite (statistiky), JSON (pravidla), named pipe IPC.
- **Základ:** fork open-source projektu `SysAdminDoc/OpenNetLimit` (MIT), maximálně osekaný na MVP.
- **Klíčová technologie:** WinDivert zachytává pakety v user-mode; limit rychlosti = token-bucket pacing + reinjekt paketů; mapování spojení na procesy přes `GetExtendedTcpTable` + WinDivert FLOW vrstvu. **Žádný vlastní kernel driver nepíšeme.**
- **Rozhodnutí uživatele (ZÁVAZNÁ):**
  - Fork + maximální osekaní na MVP.
  - Publikace na GitHub na konci (repo vytvoří vykonavatel přes `gh`).
  - Já (plánovač) jsem sepsal tento dokument; vykonavatel nic nad rámec nedělá.
- **Odkazy:** zdroj `https://github.com/SysAdminDoc/OpenNetLimit`, dokumentace WinDivert `https://github.com/basil00/WinDivert/wiki`.

---

## §3 Předpoklady stroje a ověření (SPLŇ DŘÍV, NEŽ ZAČNEŠ)

Pusť v PowerShellu a ověř výstup:

```powershell
# 3.1 .NET SDK 8 (povinné)
dotnet --version
```

- Pokud příkaz selže / hlásí "No .NET SDKs were found" → **STOP a počkej na uživatele.** Napiš do `PROGRESS.md`: "Chybí .NET SDK". Uživatel musí nainstalovat .NET 8 SDK (https://aka.ms/dotnet/download) a potvrdit. **Sám SDK neinstaluj.**

```powershell
# 3.2 git
git --version
# 3.3 GitHub CLI
gh --version
gh auth status
```

- Pokud `gh auth status` hlásí, že není přihlášen → **STOP a počkej na uživatele** (uživatel spustí `gh auth login`). Zapiš do `PROGRESS.md`.

```powershell
# 3.4 Pracovní adresář
Get-ChildItem -Force
# Musí obsahovat: EXECUTION_PLAN.md, PROGRESS.md. Nic dalšího. 
# (Pokud obsahuje něco navíc, STOP a zeptej se uživatele.)
```

```powershell
# 3.5 Admin práva (potřeba jen pro spuštění service, fáze §7)
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

Po úspěšném splnění §3 zapiš do `PROGRESS.md` a pokračuj na §4.

---

## §4 Fáze 0 – Příprava repozitáře

Cíl: pracovní adresář se stane novým git repem, jehož obsah je čistá kopie OpenNetLimit (bez jejich git historie, s novou vlastní), a ověří se výchozí build.

### 4.1 Naklonování zdroje do dočasného adresáře

```powershell
git clone https://github.com/SysAdminDoc/OpenNetLimit.git "$env:TEMP\OpenNetLimit-upstream"
```

Pokud selže (síť): retry 1×. Pořád selhává → STOP, zápis do `PROGRESS.md`.

### 4.2 Kopírování obsahu do pracovního adresáře (bez `.git`)

```powershell
Get-ChildItem -Path "$env:TEMP\OpenNetLimit-upstream" -Force | Where-Object { $_.Name -ne '.git' } | Copy-Item -Destination . -Recurse -Force
```

### 4.3 Smazání dočasné složky

```powershell
Remove-Item -Path "$env:TEMP\OpenNetLimit-upstream" -Recurse -Force
```

### 4.4 Založení nové git historie

```powershell
git init -b main
git add -A
git commit -m "Initial import of OpenNetLimit (MIT) as base for trimmed MVP"
```

### 4.5 Ověřovací build výchozího stavu

```powershell
dotnet restore OpenNetLimit.sln
dotnet build OpenNetLimit.sln
dotnet test OpenNetLimit.sln
```

- Pokud `restore`/`build` selže kvůli chybějícímu SDK → viz §3.1. Pokud selže kvůli chybě v kódu → zapiš přesně a **pokračuj dál na §5 a §6** (osekání může chybu vyřešit); po §6 musí být build zelený.
- **POZNÁMKA:** Očekávané `dotnet test` může selhat jen proto, že původní projekt má testy závislé na konkrétních podmínkách. To je v pořádku, nesnaž se je opravovat; zapiš stav. Rozhodující je zelený `dotnet build` na konci §6.

Zapiš stav do `PROGRESS.md`, commit:

```powershell
git add -A
git commit -m "Phase 0: repository setup, baseline build recorded"
```

---

## §5 Fáze 1 – Průzkum kódu a inventář (ŽÁDNÉ ZMĚNY)

Cíl: zmapovat strukturu, abys v §6 věděl, co přesně mazat. **V této fázi neměníš žádné soubory.**

### 5.1 Struktura

```powershell
Get-ChildItem -Recurse -File -Depth 3 | Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' } | Select-Object FullName
```

### 5.2 Projekt(y) v řešení

```powershell
dotnet sln OpenNetLimit.sln list
```

### 5.3 Přečti si klíčové soubory (pouze čtení)

- `README.md` (seznam funkcí a architektura)
- `src/OpenNetLimit.Service/` – hlavní vstup, registrace služeb, (případné) REST endpointy
- `src/OpenNetLimit.Engine/` – WinDivert, flow tracking, token bucket
- `src/OpenNetLimit.UI/` – WPF okna, viewmodely
- `src/OpenNetLimit.Core/` – modely, IPC protokol
- `THIRD-PARTY-NOTICES.txt` a `LICENSE`

### 5.4 Najdi soubory a řádky patřící funkcím, které se MAŽOU

Hledej tyto klíčové pojmy (case-insensitive) a zaznamenej si, ve kterých souborech jsou:

`REST`, `ApiController`, `WebApplication`, `HttpListener`, `webhook`, `plugin`, `GeoIP`, `VirusTotal`, `quota`, `alert`, `scheduler`, `Localization`, `CultureInfo`, `Theme`, `DarkTheme`, `remote`, `swagger`

```powershell
# Příklad hledání napříč zdrojovými soubory:
Get-ChildItem -Recurse -Include *.cs,*.xaml,*.csproj,*.json | Select-String -Pattern "REST|GeoIP|VirusTotal|webhook|plugin|quota|alert" -List | Select-Object Path
```

### 5.5 Zápis inventáře do `PROGRESS.md`

Do `PROGRESS.md` zapiš:
- seznam projektů v řešení,
- seznam souborů = **PONEHAT** (jádro) a **ODEJÍT** (funkce k odstranění),
- případně vlastní pozorování (např. testy závislé na něčem).

Tento inventář je závazný podklad pro §6. Neprováděj změny. Commit není potřeba (jen README/kód se neměnil), ale můžeš udělat:

```powershell
git add PROGRESS.md
git commit -m "Phase 1: exploration inventory"
```

---

## §6 Fáze 2 – Osekaní na MVP

Cíl: repozitář obsahuje jen funkce MVP a `dotnet build OpenNetLimit.sln` je zelený.

### 6.1 PONEHAT (nesmíš smazat, nesmíš rozbít)

| Oblast | Obsah |
|---|---|
| Monitoring | živé per-proces down/up rychlosti, tabulka, graf |
| Limity | per-aplikace download/upload limity (WinDivert engine, token bucket) |
| Blokování | per-aplikace blokování přístupu |
| Pravidla | ukládání pravidel do JSON (`rules.json`), import/export pravidel |
| Statistiky | SQLite (`traffic.db`), denní/hodinové agregace |
| IPC | named pipe `OpenNetLimit`, příkazy pro čtení/změnu pravidel |
| Procesy | detekce `svchost` služeb, UWP/Store aplikací, wildcard pravidla (`*\\chrome.exe`) |
| UI | WPF okna + systémová lišta (tray) |
| Service | Windows service obsluhující WinDivert a pravidla |
| Core | modely a IPC protokol |

### 6.2 ODEJÍT (odstranit ÚPLNĚ – kód, soubory, reference, DI registrace, UI prvky)

Odstraň **vše**, co souvisí s těmito funkcemi (podle inventáře z §5.4):

1. **REST API / remote administration** (WebApplication/kontrolery/endpointy, `OPENNETLIMIT_*` env var pro API klíč, remote bind)
2. **Plugin webhooks** (plugin manifesty, `eventSubscriptions`, reload endpointy)
3. **GeoIP lookup** (env `OPENNETLIMIT_GEOIP_ENABLED`, cache, HTTP volání na GeoIP poskytovatele)
4. **VirusTotal** (verifikace hashů exe)
5. **Quota management** (data caps denně/týdně/měsíčně, auto-throttle/auto-block)
6. **Bandwidth alerts** (threshold pravidla, cooldowny, tray notifikace)
7. **Rule scheduling** (časové plány dny/hodiny)
8. **Theming** (dark/light přepínač, persisted theme)
9. **Localization** (katalogy EN/ES, `OPENNETLIMIT_UI_CULTURE`, jazykový přepínač)
10. **Connection log** (rolling log spojení) — jen pokud je to samostatný modul/soubor; logování do souborů (`logs/`) v service **ponechej**.

**Pravidla pro mazání:**
- Při mazání funkce odstraň: příslušné soubory, namespace/názvy v jiných souborech, volání, DI registrace, zmínky v `.csproj`/sln, UI prvky.
- **Nemaž žádnou logiku WinDivert enginu, rate limitu, flow trackingu, monitoringu, IPC, ukládání pravidel nebo statistik.**
- **Pokud nějaká funkce z ODEJÍT sdílí kód s PONEHAT**, sdílenou část zachovej a odstraň jen specifickou část.
- **Nepřidávej nové funkce ani náhrady.** Např. schopnost kvót/alertů prostě zmizí.
- `THIRD-PARTY-NOTICES.txt` a `LICENSE` **nesmíš mazat** (viz §8).

### 6.3 Kontrola po osekání

```powershell
dotnet restore OpenNetLimit.sln
dotnet build OpenNetLimit.sln
dotnet test OpenNetLimit.sln
```

- `dotnet build` musí být **zelený**. Pokud není:
  - Chyba zaviněná nedopáleným mazáním (např. reference na smazaný typ) → **oprav jen toto** (doplň odstranění), dál nezasahuj.
  - Chyba nesouvisející s osekáním → zapiš do `PROGRESS.md` a rozhodni dle §11.
- `dotnet test`: pokud testy padají kvůli odebrání funkcí → **příslušné testy smaž** (patří k odstraněné funkci). Jinak nech běžet.

### 6.4 Doporučený build po každé větší dávce změn

Mezi mazáním si spouštěj `dotnet build` průběžně, ať najdeš chybu dřív, než jich bude moc. To je povolené (kontrola, ne změna návrhu).

### 6.5 Zápis a commit

Zapiš do `PROGRESS.md` (co smazáno, stav build/test), pak:

```powershell
git add -A
git commit -m "Phase 2: trimmed to MVP (monitoring, limits, blocking, rules, stats)"
```

---

## §7 Fáze 3 – Ověření funkčnosti (spouštění)

Cíl: ověřit, že monitoring, limity i blokování reálně fungují. **Spouštění service vyžaduje admin práva a nainstalovaný/dostupný WinDivert** (driver si WinDivert nainstaluje sám při prvním spuštění; je potřeba spustit jako administrátor).

> **DŮLEŽITÉ:** Pokud tyto kroky nelze provést (není admin okno, WinDivert se nenahraje, EDR blokuje), **nikdy nic neobcházej a nic nepřekonfigurovávej systému** (žádné zakázání Secure Boot, žádné výjimky antiviru). Zapiš stav do `PROGRESS.md` a **STOP – počkej na uživatele**.

### 7.1 Příprava: spustit PowerShell jako administrátor

Vykonavatel sám neumí bezpečně navýšit práva. Postup: vyzvi uživatele, aby otevřel **PowerShell jako administrátor** (Win → "PowerShell" → Spustit jako správce) a v něm spustil níže uvedené příkazy. Bez admin okna tento bod NEJDE dál.

### 7.2 Spuštění service (v admin okně, v pracovním adresáři)

```powershell
dotnet run --project src/OpenNetLimit.Service
```

- Ověř v logu/`%ProgramData%\OpenNetLimit\logs\`, že service naběhla a driver se nahrál (hledej klíčová slova jako `started`, `WinDivert`, `driver`). Chybu zkopíruj do `PROGRESS.md`.
- Nech service běžet (nebo ověř, že běží).

### 7.3 Spuštění UI (druhé okno, admin práva nejsou nutná)

```powershell
dotnet run --project src/OpenNetLimit.UI
```

- Ověř: v seznamu procesů jsou vidět běžící aplikace, sloupce download/upload se mění (spusť např. speedtest / stahování souboru).

### 7.4 Testovací scénáře (proveď, co jde, a výsledek zapiš)

**Scénář A – limit downloadu:** vyber proces (např. prohlížeč) → nastav download limit (např. 500 KB/s) → stáhni soubor → ověř, že reálná rychlost odpovídá limitu (přibližně, ±20 % je OK). Zruš limit.

**Scénář B – limit uploadu:** vyber proces s uploadem → nastav upload limit → ověř omezení. Zruš limit.

**Scénář C – blokování:** vyber proces → zapni blokování → ověř, že proces ztratí konektivitu. Vypni blokování.

**Scénář D – pravidla:** limit nastav, restartuj UI → ověř, že pravidlo přežilo restart (uloženo v `rules.json`).

- Pokud scénář nefunguje a chyba souvisí s osekáním (chybí registrace, špatná reference) → oprav jen toto. Jinak zapiš a řeš přes §11.

### 7.5 Zápis a commit

Zapiš výsledky všech scénářů do `PROGRESS.md`, pak:

```powershell
git add -A
git commit -m "Phase 3: functional verification of monitoring, limits, blocking"
```

---

## §8 Fáze 4 – Dokumentace pro uživatele

Cíl: repo je samostatně pochopitelné pro kohokoli.

### 8.1 Přepiš `README.md`

Zachovej formát, ale obsah zredukuj tak, že popisuje **jen současný MVP**. Struktura README:

1. **Název + 1 věta** co aplikace dělá (lokální NetLimiter: monitor, limity, blokování).
2. **Požadavky:** Windows 10/11 x64, .NET 8 Runtime, admin práva pro service, WinDivert (instaluje se automaticky při spuštění service).
3. **Stav:** odkaz, že jde o zjednodušený fork OpenNetLimit (MIT), odkaz na zdroj, výčet rozdílů (co je osekané).
4. **Build:** `dotnet restore`, `dotnet build OpenNetLimit.sln`, `dotnet test OpenNetLimit.sln`.
5. **Spuštění:** service jako admin (`dotnet run --project src/OpenNetLimit.Service`) + UI (`dotnet run --project src/OpenNetLimit.UI`).
6. **Použití:** jak nastavit limit/blokování (pravé tlačítko na proces), kde jsou data (`%ProgramData%\OpenNetLimit\`: `rules.json`, `traffic.db`, `logs/`).
7. **Upozornění:** vyžaduje admin; WinDivert je "dual-use" driver a EDR/antivirus jej může hlásit; přesnost limitu je přibližná; CPU náročnost při >1 Gbps.
8. **Licence:** MIT, WinDivert LGPL-3.0 (odkaz na `THIRD-PARTY-NOTICES.txt`).

### 8.2 Ověř, že existují a jsou v pořádku

- `LICENSE` (MIT, od původního projektu) – PONEHEJ beze změny.
- `THIRD-PARTY-NOTICES.txt` – PONEHEJ beze změny (WinDivert LGPL).
- Žádný jiný dokument (RESEARCH.md, ROADMAP.md, Roadmap_Blocked.md, CHANGELOG.md z původního projektu) **nesmí zůstat**, pokud neplatí pro MVP → pokud v repu jsou a neplatí pro MVP, **smaž je**. (Rozhodnutí: RESEARCH/ROADMAP/CHANGELOG se mažou; výjimku tvoří THIRD-PARTY-NOTICES a LICENSE.)

### 8.3 Commit

```powershell
git add -A
git commit -m "Phase 4: documentation for MVP"
```

---

## §9 Fáze 5 – Publikace na GitHub

Cíl: vytvořit veřejné repo a pushnout. Předpoklad: `gh` je přihlášený (§3.3).

### 9.1 Vytvoření repa

```powershell
gh repo create InternetLimiter --public --source . --remote origin --push
```

- **Pokud je jméno `InternetLimiter` na GitHubu obsazené** → STOP a počkej na uživatele (zeptej se na jiné jméno). Sám jiné jméno nevymýšlej.
- Pokud selže z jiného důvodu → §11.

### 9.2 Ověření

```powershell
git remote -v
gh repo view InternetLimiter
```

- Ověř: remote `origin` ukazuje na nové repo, repo je veřejné a obsahuje poslední commit.

### 9.3 Zápis do `PROGRESS.md`

Zapiš: URL repa, status. **Commit pro PROGRESS.md už nedělej** (repo už je vytvořené) – pokud chceš, pushni poslední stav:

```powershell
git add -A
git commit -m "Phase 5: publish notes" --allow-empty 2>$null
git push origin main
```

---

## §10 Údržba `PROGRESS.md` (šablona)

Po KAŽDÉ fázi přidej nový záznam podle šablony (vše v češtině, stručně a přesně):

```
## <Datum a čas> – Fáze <N>: <název>

**Stav:** ✅ hotovo / ⚠️ částečně / ❌ zablokováno

**Provedeno:**
- ...

**Build/test:** `dotnet build` → <OK/FAIL + poslední chyba>, `dotnet test` → <OK/FAIL>

**Pozorování / problémy:**
- ... (konkrétní příkazy a hlášky)

**Další krok:** <co navazuje podle EXECUTION_PLAN.md, nebo "Čekám na uživatele: ...">
```

Pravidla:
- Původní obsah `PROGRESS.md` **nesmaž**, pouze přidávej nové sekce.
- Uživatel/plánovač se podle něj vrací do kontextu. Piš tak, aby i po ztrátě tokenů bylo jasné, kde se je.
- Pokud nějaký krok zablokuješ (STOP), napiš do "Další krok" přesně, co musí uživatel udělat.

---

## §11 Záchranný protokol (co dělat při chybě)

| Situace | Reakce |
|---|---|
| Chybí .NET SDK (§3.1) | STOP. Napiš do `PROGRESS.md`. Uživatel nainstaluje SDK a potvrdí. |
| `gh` nepřihlášen (§3.3) | STOP. Uživatel spustí `gh auth login`. Zapiš. |
| `git clone` selže (síť) | Retry 1×. Pak STOP + zápis. |
| V pracovním adresáři je něco navíc (§3.4) | STOP + otázka uživateli. |
| `dotnet build` selže po osekání (§6) | Chyba ze smazání → oprav jen mazání. Jinak zapiš a STOP. |
| Service nenaběhne / driver se nenahraje (§7) | Ověř admin práva a log. Neobcházej. Zapiš a STOP (počkej na uživatele; může jít o EDR blokaci WinDivert). |
| Testovací scénář nefunguje (§7) | Chyba z osekání → oprav jen toto. Jinak zapiš a STOP. |
| `gh repo create` – jméno obsazené (§9) | STOP, zeptej se uživatele na jméno. |
| Cokoli jiného nečekaného | Zapiš přesně do `PROGRESS.md` (příkaz + výstup) a STOP – počkej na uživatele. Nikdy nehádej řešení mimo dokument. |

---

## §12 Definice hotovo (DoD)

Projekt je HOTOVÝ, když platí VŠE:

- [ ] `dotnet build OpenNetLimit.sln` je zelený.
- [ ] Repo obsahuje jen MVP funkce (§6.1), bez funkcí ze seznamu ODEJÍT (§6.2).
- [ ] Monitoring, limity up/down a blokování fungují podle scénářů §7.4 (nebo je ve `PROGRESS.md` zapsaný blokátor, na který čeká uživatel).
- [ ] `README.md` popisuje jen MVP (§8).
- [ ] `LICENSE` a `THIRD-PARTY-NOTICES.txt` jsou v repu.
- [ ] Repo je veřejné na GitHubu, `origin` je nastavené, poslední stav je pushnutý (§9).
- [ ] `PROGRESS.md` obsahuje záznam o každé fázi (§10).
