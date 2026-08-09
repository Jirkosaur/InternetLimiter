# Plán: Oprava nefunkčního limitování programů (OpenNetLimit)

## Cíl

Opravit chybu, kvůli které se limity šířky pásma vůbec neaplikují. Monitorování a atribuce provozu fungují; škrcení ne.

## Root cause

V `WinDivertInterceptor.NetworkLoop` se na **každém paketu** volá `_rateLimiter.SetLimit(pid, down, up)` (auto-reconcile pro procesy spuštěné po přidání pravidla).

`ProcessRateLimiter.SetLimit` ale dělá:

```csharp
_buckets.AddOrUpdate(processId, buckets, (_, _) => buckets);
```

Update-factory **vždy nahradí existující TokenBucket novým**. Nový `TokenBucket` startuje plný (`_tokens = CapacityBytes`, kde `CapacityBytes = max(limit, 65536)`).

→ Na každém paketu se bucket resetuje na plnou kapacitu → `GetDelay` vrací 0 → `TryConsume` vrací `true` → **všechen provoz prochází okamžitě, limit se nikdy neprojeví**.

## Změna 1 — `src/OpenNetLimit.Engine/RateLimiting/ProcessRateLimiter.cs`

Přepsat `SetLimit` (řádky 28–35), aby byl **idempotentní**: pokud bucket se stejnými limity už existuje, ponechat ho i s nabranými tokeny. Nahradit jen při změně hodnoty.

```csharp
public void SetLimit(uint processId, long downloadBytesPerSecond, long uploadBytesPerSecond)
{
    var buckets = new ProcessBuckets(
        downloadBytesPerSecond > 0 ? new TokenBucket(downloadBytesPerSecond) : null,
        uploadBytesPerSecond > 0 ? new TokenBucket(uploadBytesPerSecond) : null);

    _buckets.AddOrUpdate(processId, buckets, (_, existing) =>
        SameLimits(existing, buckets) ? existing : buckets);
}

private static bool SameLimits(ProcessBuckets a, ProcessBuckets b)
{
    return (a.Download?.RefillBytesPerSecond ?? 0) == (b.Download?.RefillBytesPerSecond ?? 0)
        && (a.Upload?.RefillBytesPerSecond ?? 0) == (b.Upload?.RefillBytesPerSecond ?? 0);
}
```

- `TokenBucket` má veřejnou property `RefillBytesPerSecond` — slouží k porovnání.
- Per-packet volání v `NetworkLoop` **zůstává** (po fixu je neškodné a stále pokrývá nově spuštěné procesy).
- `RemoveLimit`, `RemoveAll`, `HasLimit`, `TryConsume`, `GetDelay` se nemění.

## Změna 2 — regresní testy

Testy `ProcessRateLimiterTests` už existují v `tests/OpenNetLimit.Tests/TokenBucketTests.cs` (třída na řádcích 65–131). **Přidat do této třídy** tyto `[Fact]` metody:

```csharp
[Fact]
public void SetLimit_SameLimits_PreservesBucketState()
{
    var limiter = new ProcessRateLimiter();
    limiter.SetLimit(1, 1000, 1000);

    limiter.TryConsume(1, 65536, false);
    Assert.True(limiter.GetDelay(1, 1000, false) > TimeSpan.Zero);

    limiter.SetLimit(1, 1000, 1000);

    Assert.True(limiter.GetDelay(1, 1000, false) > TimeSpan.Zero);
}

[Fact]
public void SetLimit_ChangedLimit_ReplacesBucket()
{
    var limiter = new ProcessRateLimiter();
    limiter.SetLimit(1, 1000, 1000);

    limiter.TryConsume(1, 65536, false);
    limiter.SetLimit(1, 2000, 1000);

    Assert.Equal(TimeSpan.Zero, limiter.GetDelay(1, 1000, false));
}

[Fact]
public void SetLimit_CalledRepeatedly_DoesNotResetBucket()
{
    var limiter = new ProcessRateLimiter();
    limiter.SetLimit(1, 1000, 1000);

    for (int i = 0; i < 100; i++)
        limiter.SetLimit(1, 1000, 1000);

    limiter.TryConsume(1, 65536, false);

    for (int i = 0; i < 100; i++)
        limiter.SetLimit(1, 1000, 1000);

    Assert.True(limiter.GetDelay(1, 1000, false) > TimeSpan.Zero);
}
```

- Třetí test reprodukuje přesně chování `NetworkLoop` (opakované `SetLimit` se stejnou hodnotou) a **projde jen s fixem**.
- Použít stávající styl: `using OpenNetLimit.Engine.RateLimiting;` + `using Xunit;` (už jsou naimportované v souboru).

## Ověření

1. `dotnet build` v repo rootu (`C:\Users\jirin\Desktop\InternetLimiter`) — nesmí být chyby.
2. `dotnet test tests/OpenNetLimit.Tests` — všechny testy (hlavně 3 nové) musí projít.
3. Ruční test: spustit aplikaci, nastavit nízký limit (např. 200 KB/s) na proces se stahováním → rychlost musí reálně klesnout (předtím neklesala).

## Co nedělat

- Neměnit chování `NetworkLoop` (per-packet `SetLimit` má zůstat).
- Neměnit `TokenBucket` (kapacita `max(limit, 65536)` zůstává).
- Neměnit `PacketScheduler` / `WinDivertInterceptor` zbytečně — stačí fix v `ProcessRateLimiter.SetLimit`.
