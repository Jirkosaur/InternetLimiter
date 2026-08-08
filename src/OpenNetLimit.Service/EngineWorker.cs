using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.IPC;
using OpenNetLimit.Service.Storage;

namespace OpenNetLimit.Service;

public class EngineWorker : BackgroundService
{
    private readonly IPacketInterceptor _interceptor;
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly PipeServer _pipeServer;
    private readonly ILogger<EngineWorker> _logger;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private RuleReconciler? _reconciler;
    private TrafficStatsDb? _statsDb;
    private Timer? _statsTimer;
    private Timer? _purgeTimer;
    private Timer? _flowPurgeTimer;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit");

    private static readonly string RulesPath = Path.Combine(DataDir, "rules.json");
    private static readonly string StatsDbPath = Path.Combine(DataDir, "traffic.db");
    private static readonly string LastErrorPath = Path.Combine(DataDir, "last-error.txt");

    public EngineWorker(
        IPacketInterceptor interceptor,
        IRuleEngine ruleEngine,
        IRateLimiter rateLimiter,
        IFlowTracker flowTracker,
        ITrafficMonitor trafficMonitor,
        PipeServer pipeServer,
        ILogger<EngineWorker> logger)
    {
        _interceptor = interceptor;
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _flowTracker = flowTracker;
        _trafficMonitor = trafficMonitor;
        _pipeServer = pipeServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenNetLimit engine starting");

        if (!ValidatePrerequisites())
        {
            _logger.LogCritical("Prerequisite validation failed — engine will not start");
            return;
        }

        ClearLastError();
        EnsureDataDirectory();

        _reconciler = new RuleReconciler(_ruleEngine, _rateLimiter, _flowTracker);
        if (_ruleEngine is RuleEngine concreteEngine)
        {
            concreteEngine.OnRulesChanged += () =>
            {
                try { _reconciler.Reconcile(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Rule reconciliation failed"); }
            };
        }

        LoadRules();
        _reconciler.Reconcile();

        try
        {
            await _interceptor.StartAsync(stoppingToken);
            _logger.LogInformation("Packet interceptor started");
            CheckWinDivertDriverSignature();
        }
        catch (Exception ex)
        {
            var hint = "Failed to start packet interceptor.\n" +
                       "Possible causes:\n" +
                       "  - WinDivert driver not found or inaccessible\n" +
                       "  - HVCI (Memory Integrity) is enabled and blocking the driver\n" +
                       "  - Antivirus/EDR is blocking WinDivert64.sys\n" +
                       "  - Service is not running with administrator privileges\n" +
                       $"Error: {ex.Message}";
            RecordLastError(hint);
            _logger.LogCritical(ex, "Failed to start packet interceptor — check {ErrorFile} for troubleshooting steps", LastErrorPath);
            return;
        }

        try
        {
            _statsDb = new TrafficStatsDb(StatsDbPath);
            _statsTimer = new Timer(_ => RecordStats(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            _purgeTimer = new Timer(_ => _statsDb.PurgeOlderThan(90), null, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
            _logger.LogInformation("Traffic statistics database initialized at {Path}", StatsDbPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize stats database — statistics will be unavailable");
        }

        _flowPurgeTimer = new Timer(_ => PurgeStaleFlows(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _ = Task.Run(() => RunPipeServer(stoppingToken), stoppingToken);
        _logger.LogInformation("IPC pipe server started");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("OpenNetLimit engine stopping");
        }

        await ShutdownGracefully();
    }

    private bool ValidatePrerequisites()
    {
        bool valid = true;

        if (!IsRunningAsAdmin())
        {
            _logger.LogError("OpenNetLimit requires administrator privileges to load the WinDivert driver");
            RecordLastError("Service not running as administrator");
            valid = false;
        }

        return valid;
    }

    private void CheckWinDivertDriverSignature()
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(SharpDivert.WinDivert).Assembly.Location);
            if (assemblyDir is null) return;

            var driverPath = Path.Combine(assemblyDir, "WinDivert64.sys");
            if (!File.Exists(driverPath))
                driverPath = Path.Combine(assemblyDir, "WinDivert.sys");
            if (!File.Exists(driverPath))
            {
                _logger.LogWarning("WinDivert driver binary not found for signature check");
                return;
            }

            try
            {
                using var baseCert = X509Certificate.CreateFromSignedFile(driverPath);
                using var cert = new X509Certificate2(baseCert);
                if (cert.NotAfter < DateTime.Now)
                {
                    _logger.LogWarning("WinDivert driver signature EXPIRED on {ExpiryDate}.", cert.NotAfter);
                }
                else
                {
                    _logger.LogInformation("WinDivert driver signature valid until {ExpiryDate}", cert.NotAfter);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify WinDivert driver signature.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WinDivert signature check skipped");
        }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void EnsureDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data directory {DataDir}", DataDir);
        }
    }

    private void LoadRules()
    {
        try
        {
            _ruleEngine.LoadRules(RulesPath);
            _logger.LogInformation("Loaded {Count} rules from {Path}",
                _ruleEngine.GetAllRules().Count, RulesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load rules from {Path} — starting with empty rule set", RulesPath);
            RecordLastError($"Failed to load rules: {ex.Message}");
        }
    }

    private void SaveRules()
    {
        try
        {
            _ruleEngine.SaveRules(RulesPath);
            _logger.LogInformation("Saved rules to {Path}", RulesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save rules to {Path}", RulesPath);
        }
    }

    private void PurgeStaleFlows()
    {
        try
        {
            _flowTracker.PurgeStale(TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge stale flows");
        }
    }

    private void RecordStats()
    {
        try
        {
            var snapshot = _trafficMonitor.TakeSnapshot();
            _statsDb?.RecordSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record traffic statistics");
        }
    }

    private async Task ShutdownGracefully()
    {
        _statsTimer?.Dispose();
        _purgeTimer?.Dispose();
        _flowPurgeTimer?.Dispose();

        try
        {
            await _interceptor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping packet interceptor during shutdown");
        }

        SaveRules();
        _statsDb?.Dispose();
        _logger.LogInformation("OpenNetLimit engine stopped");
    }

    private async Task RunPipeServer(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pipeServer.StartAsync(ct);
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay = Math.Min(1000 * (1 << Math.Min(consecutiveFailures - 1, 5)), 30_000);
                _logger.LogError(ex, "IPC pipe server crashed (attempt {Attempt}), restarting in {Delay}ms",
                    consecutiveFailures, delay);
                RecordLastError($"IPC pipe server crashed: {ex.Message}");
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private DiagnosticInfo GetDiagnosticInfo()
    {
        return new DiagnosticInfo
        {
            Running = _interceptor.IsRunning,
            ActiveFlows = _flowTracker.GetActiveConnections().Count,
            ActiveRules = _ruleEngine.GetAllRules().Count,
            StartedAt = _startedAt,
            PacketsDelayed = _interceptor.TotalDelayed,
            PacketsDropped = _interceptor.TotalDropped,
            PacketsSent = _interceptor.TotalSent,
            PacketsBlocked = _interceptor.TotalBlocked
        };
    }

    private void RecordLastError(string message)
    {
        try
        {
            EnsureDataDirectory();
            File.WriteAllText(LastErrorPath, $"{DateTime.UtcNow:O} {message}");
        }
        catch
        {
            // Best-effort; don't fail the service over error recording
        }
    }

    private void ClearLastError()
    {
        try
        {
            if (File.Exists(LastErrorPath))
                File.Delete(LastErrorPath);
        }
        catch
        {
            // Best-effort
        }
    }
}
