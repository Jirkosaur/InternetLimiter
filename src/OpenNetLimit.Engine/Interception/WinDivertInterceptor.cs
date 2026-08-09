using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using RuleAction = OpenNetLimit.Core.Models.RuleAction;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using SharpDivert;

namespace OpenNetLimit.Engine.Interception;

public sealed class WinDivertInterceptor : IPacketInterceptor
{
    private readonly IFlowTracker _flowTracker;
    private readonly IRateLimiter _rateLimiter;
    private readonly IRuleEngine _ruleEngine;
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly PacketScheduler _scheduler = new();
    private readonly DnsDomainCache _dnsCache = new();
    private long _totalBlocked;
    private long _flowEvents;
    private long _flowRegistrations;
    private long _flowDeletions;
    private long _lookupHits;
    private long _lookupMisses;

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "services", "lsass", "csrss", "wininit", "smss",
        "dns", "dhcp", "dnscache", "System", "ntoskrnl"
    };

    private WinDivert? _networkHandle;
    private WinDivert? _flowHandle;
    private CancellationTokenSource? _cts;
    private Task? _networkTask;
    private Task? _flowTask;

    private volatile bool _isRunning;
    public bool IsRunning => _isRunning;
    public PacketScheduler Scheduler => _scheduler;

    public long TotalBlocked => Volatile.Read(ref _totalBlocked);
    public long TotalDelayed => _scheduler.TotalDelayed;
    public long TotalDropped => _scheduler.TotalDropped;
    public long TotalSent => _scheduler.TotalSent;

    public IReadOnlyList<object> GetRecentConnectionLog(int maxCount) =>
        Array.Empty<object>();

    public WinDivertInterceptor(
        IFlowTracker flowTracker,
        IRateLimiter rateLimiter,
        IRuleEngine ruleEngine,
        ITrafficMonitor trafficMonitor)
    {
        _flowTracker = flowTracker;
        _rateLimiter = rateLimiter;
        _ruleEngine = ruleEngine;
        _trafficMonitor = trafficMonitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;

        // Set _isRunning before launching tasks to prevent a racing StopAsync
        // from seeing IsRunning == false and returning early while tasks are starting
        _isRunning = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _flowHandle = new WinDivert("true", WinDivert.Layer.Flow, 0, WinDivert.Flag.Sniff | WinDivert.Flag.RecvOnly);
            _networkHandle = new WinDivert("true", WinDivert.Layer.Network, 0, default);
        }
        catch
        {
            _isRunning = false;
            _flowHandle?.Dispose();
            _flowHandle = null;
            _networkHandle?.Dispose();
            _networkHandle = null;
            throw;
        }

        _scheduler.SetHandle(_networkHandle);

        _flowTask = Task.Factory.StartNew(
            () => FlowLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _networkTask = Task.Factory.StartNew(
            () => NetworkLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _cts?.Cancel();

        try
        {
            if (_networkTask is not null)
                await _networkTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        try
        {
            if (_flowTask is not null)
                await _flowTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        _scheduler.Dispose();
        _networkHandle?.Dispose();
        _flowHandle?.Dispose();
        _networkHandle = null;
        _flowHandle = null;

        _isRunning = false;
    }

    private void FlowLoop(CancellationToken ct)
    {
        var buffer = new Memory<byte>(new byte[65535]);
        var addrBuffer = new Memory<WinDivertAddress>(new WinDivertAddress[1]);
        int consecutiveErrors = 0;
        long lastLogTicks = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (recvLen, _) = _flowHandle!.RecvEx(buffer.Span, addrBuffer.Span);
                Interlocked.Increment(ref _flowEvents);
                consecutiveErrors = 0;
                ref var addr = ref addrBuffer.Span[0];

                var flowData = addr.Flow;
                var protocol = flowData.Protocol == 6 ? TransportProtocol.Tcp :
                               flowData.Protocol == 17 ? TransportProtocol.Udp :
                               TransportProtocol.Other;

                var localAddr = ParseIPv6Addr(flowData.LocalAddr);
                var remoteAddr = ParseIPv6Addr(flowData.RemoteAddr);
                var flowKey = new FlowKey(
                    protocol,
                    localAddr,
                    flowData.LocalPort,
                    remoteAddr,
                    flowData.RemotePort);

                if (addr.Event == WinDivert.Event.FlowEstablished)
                {
                    Interlocked.Increment(ref _flowRegistrations);
                    string processName = ResolveProcessName(flowData.ProcessId);
                    string? processPath = ResolveProcessPath(flowData.ProcessId);
                    _flowTracker.RegisterFlow(flowKey, flowData.ProcessId, processName, processPath);
                }
                else if (addr.Event == WinDivert.Event.FlowDeleted)
                {
                    Interlocked.Increment(ref _flowDeletions);
                    _flowTracker.UnregisterFlow(flowKey);
                }

                if (Environment.TickCount64 - lastLogTicks > 10_000)
                {
                    lastLogTicks = Environment.TickCount64;
                    Trace.TraceInformation($"FlowLoop: events={_flowEvents} reg={_flowRegistrations} del={_flowDeletions}");
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Trace.TraceError($"FlowLoop error ({consecutiveErrors}): {ex.Message}");
                if (consecutiveErrors >= 10)
                {
                    Trace.TraceError("FlowLoop: too many consecutive errors, stopping");
                    _isRunning = false;
                    break;
                }
                Thread.Sleep(Math.Min(consecutiveErrors * 100, 1000));
            }
        }
    }

    private void NetworkLoop(CancellationToken ct)
    {
        var buffer = new Memory<byte>(new byte[65535]);
        var addrBuffer = new Memory<WinDivertAddress>(new WinDivertAddress[1]);
        int consecutiveErrors = 0;
        long lastLogTicks = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (recvLen, _) = _networkHandle!.RecvEx(buffer.Span, addrBuffer.Span);
                consecutiveErrors = 0;
                ref var addr = ref addrBuffer.Span[0];
                var packet = buffer[..(int)recvLen];

                var parsed = ParsePacket(packet);
                if (parsed is null)
                {
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }

                var (flowKey, payloadLength, _) = parsed.Value;
                bool isOutbound = addr.Outbound;

                var processId = _flowTracker.LookupProcessId(flowKey);
                if (processId is null)
                {
                    processId = TryResolveProcessIdFromSystem(flowKey);
                    if (processId is not null)
                    {
                        string pName = ResolveProcessName(processId.Value);
                        string? pPath = ResolveProcessPath(processId.Value);
                        _flowTracker.RegisterFlow(flowKey, processId.Value, pName, pPath);
                    }
                }

                if (processId is null)
                {
                    Interlocked.Increment(ref _lookupMisses);
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }
                Interlocked.Increment(ref _lookupHits);

                var connection = _flowTracker.LookupConnection(flowKey);
                string processName = connection?.ProcessName ?? "unknown";

                if (connection is not null)
                {
                    if (isOutbound)
                        connection.AddBytesSent(payloadLength);
                    else
                        connection.AddBytesReceived(payloadLength);
                }

                _trafficMonitor.RecordBytes(processId.Value, processName, payloadLength, isOutbound, connection?.ProcessPath);

                // Detect DNS responses (UDP from port 53) and cache domain→IP mappings
                if (!isOutbound && flowKey.Protocol == TransportProtocol.Udp && flowKey.RemotePort == 53 && payloadLength > 12)
                {
                    try
                    {
                        var dnsRecords = DnsResponseParser.ParseResponse(parsed.Value.payloadData.Span);
                        foreach (var record in dnsRecords)
                            _dnsCache.RecordMapping(record.Address, record.Domain, record.Ttl);
                    }
                    catch
                    {
                        // Best-effort DNS parsing — don't disrupt packet flow
                    }
                }

                var remoteAddr = isOutbound ? flowKey.RemoteAddress : flowKey.LocalAddress;
                var remotePort = isOutbound ? (int)flowKey.RemotePort : (int)flowKey.LocalPort;
                var protocolStr = flowKey.Protocol.ToString();
                var resolvedDomain = _dnsCache.LookupDomain(remoteAddr);
                var matchingRule = _ruleEngine.FindMatchingRule(processName, connection?.ProcessPath,
                    remoteAddr, remotePort, protocolStr, resolvedDomain: resolvedDomain);

                if (matchingRule is not null && !ProtectedProcesses.Contains(processName) && matchingRule.IsActiveNow())
                {
                    if (matchingRule.Action == RuleAction.Block)
                    {
                        Interlocked.Increment(ref _totalBlocked);
                        continue;
                    }

                    if (matchingRule.Action == RuleAction.Limit)
                    {
                        long downLimit = matchingRule.Direction is RuleDirection.Both or RuleDirection.Download
                            ? matchingRule.DownloadBytesPerSecond : 0;
                        long upLimit = matchingRule.Direction is RuleDirection.Both or RuleDirection.Upload
                            ? matchingRule.UploadBytesPerSecond : 0;

                        if (downLimit > 0 || upLimit > 0)
                        {
                            _rateLimiter.SetLimit(processId.Value, downLimit, upLimit);
                        }
                    }
                }

                if (_rateLimiter.HasLimit(processId.Value) && !ProtectedProcesses.Contains(processName))
                {
                    var delay = _rateLimiter.GetDelay(processId.Value, payloadLength, isOutbound);
                    bool consumed = _rateLimiter.TryConsume(processId.Value, payloadLength, isOutbound);
                    if (!consumed || delay > TimeSpan.Zero)
                    {
                        var effectiveDelay = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(5);
                        _scheduler.Enqueue(processId.Value, packet.Span, addrBuffer.Span, effectiveDelay);
                        continue;
                    }
                }

                _networkHandle.SendEx(packet.Span, addrBuffer.Span);

                if (Environment.TickCount64 - lastLogTicks > 10_000)
                {
                    lastLogTicks = Environment.TickCount64;
                    Trace.TraceInformation($"NetworkLoop: hits={_lookupHits} misses={_lookupMisses}");
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Trace.TraceError($"NetworkLoop error ({consecutiveErrors}): {ex.Message}");
                if (consecutiveErrors >= 10)
                {
                    Trace.TraceError("NetworkLoop: too many consecutive errors, stopping");
                    _isRunning = false;
                    break;
                }
                Thread.Sleep(Math.Min(consecutiveErrors * 100, 1000));
            }
        }
    }

    private static unsafe (FlowKey flowKey, int payloadLength, Memory<byte> payloadData)? ParsePacket(Memory<byte> packet)
    {
        var parser = new WinDivertPacketParser(packet);
        foreach (var result in parser)
        {
            IPAddress srcAddr, dstAddr;
            if (result.IPv4Hdr != null)
            {
                Span<byte> srcBytes = stackalloc byte[4];
                Span<byte> dstBytes = stackalloc byte[4];
                new ReadOnlySpan<byte>(&result.IPv4Hdr->SrcAddr, 4).CopyTo(srcBytes);
                new ReadOnlySpan<byte>(&result.IPv4Hdr->DstAddr, 4).CopyTo(dstBytes);
                srcAddr = NormalizeIp(new IPAddress(srcBytes));
                dstAddr = NormalizeIp(new IPAddress(dstBytes));
            }
            else if (result.IPv6Hdr != null)
            {
                Span<byte> srcBytes = stackalloc byte[16];
                Span<byte> dstBytes = stackalloc byte[16];
                new ReadOnlySpan<byte>(&result.IPv6Hdr->SrcAddr, 16).CopyTo(srcBytes);
                new ReadOnlySpan<byte>(&result.IPv6Hdr->DstAddr, 16).CopyTo(dstBytes);
                srcAddr = NormalizeIp(new IPAddress(srcBytes));
                dstAddr = NormalizeIp(new IPAddress(dstBytes));
            }
            else
            {
                return null;
            }

            TransportProtocol protocol;
            ushort srcPort, dstPort;

            if (result.TCPHdr != null)
            {
                protocol = TransportProtocol.Tcp;
                srcPort = result.TCPHdr->SrcPort;
                dstPort = result.TCPHdr->DstPort;
            }
            else if (result.UDPHdr != null)
            {
                protocol = TransportProtocol.Udp;
                srcPort = result.UDPHdr->SrcPort;
                dstPort = result.UDPHdr->DstPort;
            }
            else
            {
                return null;
            }

            int payloadLength = result.Data.Length;
            var payloadData = result.Data;

            var flowKey = new FlowKey(protocol, srcAddr, srcPort, dstAddr, dstPort);
            return (flowKey, payloadLength, payloadData);
        }

        return null;
    }

    private static IPAddress NormalizeIp(IPAddress ip)
        => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static unsafe IPAddress ParseIPv6Addr(IPv6Addr addr)
    {
        Span<byte> bytes = stackalloc byte[16];
        var words = (uint*)&addr;
        for (int i = 0; i < 4; i++)
        {
            uint w = words[i];
            bytes[i * 4 + 0] = (byte)(w >> 24);
            bytes[i * 4 + 1] = (byte)(w >> 16);
            bytes[i * 4 + 2] = (byte)(w >> 8);
            bytes[i * 4 + 3] = (byte)w;
        }
        return NormalizeIp(new IPAddress(bytes));
    }

    private static string ResolveProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return $"PID-{processId}";
        }
    }

    private static string? ResolveProcessPath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    private enum UDP_TABLE_CLASS
    {
        UDP_TABLE_BASIC,
        UDP_TABLE_OWNER_PID,
        UDP_TABLE_OWNER_MODULE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public byte localPort1;
        public byte localPort2;
        public byte localPort3;
        public byte localPort4;
        public uint remoteAddr;
        public byte remotePort1;
        public byte remotePort2;
        public byte remotePort3;
        public byte remotePort4;
        public uint owningPid;

        public ushort LocalPort => (ushort)((localPort1 << 8) + localPort2);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public byte localPort1;
        public byte localPort2;
        public byte localPort3;
        public byte localPort4;
        public uint owningPid;

        public ushort LocalPort => (ushort)((localPort1 << 8) + localPort2);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, uint ulAf, TCP_TABLE_CLASS tableClass, uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, uint ulAf, UDP_TABLE_CLASS tableClass, uint reserved = 0);

    private static uint? TryResolveProcessIdFromSystem(FlowKey flowKey)
    {
        try
        {
            ushort targetPort = flowKey.LocalPort;
            int AF_INET = 2; // AF_INET
            int size = 0;

            if (flowKey.Protocol == TransportProtocol.Tcp)
            {
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, (uint)AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
                if (size > 0)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (GetExtendedTcpTable(buffer, ref size, false, (uint)AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                        {
                            int numEntries = Marshal.ReadInt32(buffer);
                            IntPtr rowPtr = IntPtr.Add(buffer, 4);
                            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                            for (int i = 0; i < numEntries; i++)
                            {
                                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                                if (row.LocalPort == targetPort && row.owningPid > 0)
                                    return row.owningPid;
                                rowPtr = IntPtr.Add(rowPtr, rowSize);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            else if (flowKey.Protocol == TransportProtocol.Udp)
            {
                GetExtendedUdpTable(IntPtr.Zero, ref size, false, (uint)AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
                if (size > 0)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (GetExtendedUdpTable(buffer, ref size, false, (uint)AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0) == 0)
                        {
                            int numEntries = Marshal.ReadInt32(buffer);
                            IntPtr rowPtr = IntPtr.Add(buffer, 4);
                            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                            for (int i = 0; i < numEntries; i++)
                            {
                                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                                if (row.LocalPort == targetPort && row.owningPid > 0)
                                    return row.owningPid;
                                rowPtr = IntPtr.Add(rowPtr, rowSize);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public void Dispose()
    {
        // Use ConfigureAwait(false) throughout StopAsync to avoid deadlock
        // when Dispose is called from a synchronization context
        var task = StopAsync();
        if (!task.IsCompleted)
            task.ConfigureAwait(false).GetAwaiter().GetResult();
        _cts?.Dispose();
    }
}
