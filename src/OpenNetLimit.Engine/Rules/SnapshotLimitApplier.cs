using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public static class SnapshotLimitApplier
{
    public static void ApplyLimits(IEnumerable<ProcessTrafficInfo> processes, IRuleEngine ruleEngine)
    {
        foreach (var proc in processes)
        {
            var rule = ruleEngine.FindMatchingRule(proc.ProcessName, proc.ProcessPath);
            if (rule is null || rule.Action != RuleAction.Limit || !rule.IsActiveNow())
                continue;

            if (rule.Direction is RuleDirection.Both or RuleDirection.Download)
                proc.DownloadLimitBytesPerSecond = rule.DownloadBytesPerSecond;
            if (rule.Direction is RuleDirection.Both or RuleDirection.Upload)
                proc.UploadLimitBytesPerSecond = rule.UploadBytesPerSecond;
        }
    }
}
