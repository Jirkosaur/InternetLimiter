using System.Net;
using OpenNetLimit.Core;

namespace OpenNetLimit.Core.Models;

public enum RuleAction
{
    Limit,
    Block,
    Allow
}

public enum RuleDirection
{
    Both,
    Download,
    Upload
}

public class BandwidthRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public string? ProcessName { get; set; }
    public string? ProcessPath { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Limit;
    public RuleDirection Direction { get; set; } = RuleDirection.Both;

    public long DownloadBytesPerSecond { get; set; }
    public long UploadBytesPerSecond { get; set; }

    public string? RemoteAddressFilter { get; set; }
    public int? RemotePortFilter { get; set; }
    public string? ProtocolFilter { get; set; }
    public string[]? CountryFilter { get; set; }
    public string? DnsDomainFilter { get; set; }

    public DateTime? ActiveFrom { get; set; }
    public DateTime? ActiveUntil { get; set; }

    public string? ProfileName { get; set; }

    public string? GroupName { get; set; }

    public int Priority { get; set; }

    public bool MatchesProcess(string processName, string? processPath)
    {
        if (ProcessPath is not null && processPath is not null)
        {
            if (ProcessPath.Contains('*') || ProcessPath.Contains('?'))
                return WildcardMatcher.IsMatch(processPath, ProcessPath);
            return processPath.Equals(ProcessPath, StringComparison.OrdinalIgnoreCase);
        }

        if (ProcessName is not null)
            return processName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    public bool MatchesConnection(IPAddress? remoteAddress, int? remotePort, string? protocol)
    {
        if (ProtocolFilter is not null && protocol is not null &&
            !ProtocolFilter.Equals(protocol, StringComparison.OrdinalIgnoreCase))
            return false;

        if (RemotePortFilter.HasValue && remotePort.HasValue && RemotePortFilter.Value != remotePort.Value)
            return false;

        if (RemoteAddressFilter is not null && remoteAddress is not null)
        {
            if (RemoteAddressFilter.Contains('/'))
            {
                if (!MatchesCidr(remoteAddress, RemoteAddressFilter))
                    return false;
            }
            else
            {
                if (!IPAddress.TryParse(RemoteAddressFilter, out var filterIp) ||
                    !filterIp.Equals(remoteAddress))
                    return false;
            }
        }

        return true;
    }

    public bool HasConnectionFilters =>
        RemoteAddressFilter is not null || RemotePortFilter.HasValue ||
        ProtocolFilter is not null || CountryFilter is { Length: > 0 } ||
        DnsDomainFilter is not null;

    public bool MatchesDomain(string? resolvedDomain)
    {
        if (DnsDomainFilter is null)
            return true;
        if (resolvedDomain is null)
            return false;

        if (DnsDomainFilter.StartsWith("*."))
        {
            var suffix = DnsDomainFilter[1..]; // ".example.com"
            return resolvedDomain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                   resolvedDomain.Equals(DnsDomainFilter[2..], StringComparison.OrdinalIgnoreCase);
        }

        return resolvedDomain.Equals(DnsDomainFilter, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesCountry(string? countryCode)
    {
        if (CountryFilter is not { Length: > 0 })
            return true;
        if (countryCode is null)
            return false;
        return CountryFilter.Any(c => c.Equals(countryCode, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefixLen))
            return false;

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length)
            return false;

        var fullBytes = prefixLen / 8;
        var remainingBits = prefixLen % 8;

        for (int i = 0; i < fullBytes && i < networkBytes.Length; i++)
        {
            if (networkBytes[i] != addressBytes[i])
                return false;
        }

        if (remainingBits > 0 && fullBytes < networkBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((networkBytes[fullBytes] & mask) != (addressBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    public BandwidthRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        ProcessName = ProcessName,
        ProcessPath = ProcessPath,
        Action = Action,
        Direction = Direction,
        DownloadBytesPerSecond = DownloadBytesPerSecond,
        UploadBytesPerSecond = UploadBytesPerSecond,
        RemoteAddressFilter = RemoteAddressFilter,
        RemotePortFilter = RemotePortFilter,
        ProtocolFilter = ProtocolFilter,
        CountryFilter = CountryFilter?.ToArray(),
        DnsDomainFilter = DnsDomainFilter,
        ActiveFrom = ActiveFrom,
        ActiveUntil = ActiveUntil,
        ProfileName = ProfileName,
        GroupName = GroupName,
        Priority = Priority
    };

    public bool IsActiveNow()
    {
        if (!Enabled) return false;
        var now = DateTime.UtcNow;
        if (ActiveFrom.HasValue && now < ActiveFrom.Value) return false;
        if (ActiveUntil.HasValue && now > ActiveUntil.Value) return false;
        return true;
    }
}
