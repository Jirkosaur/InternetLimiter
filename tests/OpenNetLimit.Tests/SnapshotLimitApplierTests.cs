using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

public class SnapshotLimitApplierTests
{
    private static ProcessTrafficInfo MakeProcess(string name = "chrome") =>
        new() { ProcessId = 100, ProcessName = name, ProcessPath = @"C:\chrome.exe" };

    [Fact]
    public void ApplyLimits_LimitRule_BothDirections()
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Action = RuleAction.Limit,
            Direction = RuleDirection.Both,
            DownloadBytesPerSecond = 50_000,
            UploadBytesPerSecond = 25_000
        });

        var proc = MakeProcess();
        SnapshotLimitApplier.ApplyLimits([proc], rules);

        Assert.Equal(50_000, proc.DownloadLimitBytesPerSecond);
        Assert.Equal(25_000, proc.UploadLimitBytesPerSecond);
    }

    [Fact]
    public void ApplyLimits_DownloadOnly_UploadStaysNull()
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Action = RuleAction.Limit,
            Direction = RuleDirection.Download,
            DownloadBytesPerSecond = 50_000
        });

        var proc = MakeProcess();
        SnapshotLimitApplier.ApplyLimits([proc], rules);

        Assert.Equal(50_000, proc.DownloadLimitBytesPerSecond);
        Assert.Null(proc.UploadLimitBytesPerSecond);
    }

    [Fact]
    public void ApplyLimits_NoMatchingRule_StaysNull()
    {
        var rules = new RuleEngine();

        var proc = MakeProcess();
        SnapshotLimitApplier.ApplyLimits([proc], rules);

        Assert.Null(proc.DownloadLimitBytesPerSecond);
        Assert.Null(proc.UploadLimitBytesPerSecond);
    }

    [Fact]
    public void ApplyLimits_DisabledRule_Ignored()
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Action = RuleAction.Limit,
            Enabled = false,
            DownloadBytesPerSecond = 50_000
        });

        var proc = MakeProcess();
        SnapshotLimitApplier.ApplyLimits([proc], rules);

        Assert.Null(proc.DownloadLimitBytesPerSecond);
        Assert.Null(proc.UploadLimitBytesPerSecond);
    }

    [Fact]
    public void ApplyLimits_BlockRule_Ignored()
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Action = RuleAction.Block,
            DownloadBytesPerSecond = 50_000
        });

        var proc = MakeProcess();
        SnapshotLimitApplier.ApplyLimits([proc], rules);

        Assert.Null(proc.DownloadLimitBytesPerSecond);
        Assert.Null(proc.UploadLimitBytesPerSecond);
    }
}
