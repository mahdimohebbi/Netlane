using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Netlane.Core;

public sealed record LocalNetwork(string ConnectionType, string LocalIp, IReadOnlyList<string> DnsServers, bool VpnLikely);

public static class NetworkInfo
{
    private static readonly string[] VpnHints =
        ["vpn", "tap-windows", "wireguard", "wintun", "openvpn", "anyconnect", "fortinet", "tailscale", "zerotier", "proton", "nord"];

    private static readonly string[] IgnoredTunnels = ["teredo", "isatap", "6to4", "loopback"];

    private static bool IsUp(NetworkInterface nic) =>
        nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback;

    private static bool IsPhysical(NetworkInterface nic) => nic.NetworkInterfaceType is
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or
        NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.Wireless80211 or
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2;

    private static bool LooksLikeVpn(NetworkInterface nic)
    {
        var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        if (IgnoredTunnels.Any(text.Contains)) return false;
        return nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel || VpnHints.Any(text.Contains);
    }

    public static LocalNetwork Read()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return new LocalNetwork("—", "—", [], false); }

        var up = nics.Where(IsUp).ToList();
        var vpn = up.Any(LooksLikeVpn);
        // The primary link is the physical interface that has a default gateway.
        var primary = up.Where(IsPhysical).FirstOrDefault(n => SafeProps(n)?.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)) == true)
                      ?? up.FirstOrDefault(IsPhysical);

        if (primary is null) return new LocalNetwork(up.Count > 0 ? "Other" : "Offline", "—", [], vpn);

        var type = primary.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
            _ => "Ethernet",
        };
        var props = SafeProps(primary);
        var localIp = props?.UnicastAddresses
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
            ?.ToString() ?? "—";
        var dns = ((IEnumerable<IPAddress>?)props?.DnsAddresses ?? [])
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(a => a.ToString())
            .Distinct()
            .Take(3)
            .ToList();
        return new LocalNetwork(type, localIp, dns, vpn);
    }

    /// Total bytes received/sent on physical interfaces (VPN tunnels excluded so traffic isn't counted twice).
    public static (long Received, long Sent) TrafficCounters()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => IsUp(n) && IsPhysical(n)))
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException) { }
            }
        }
        catch (NetworkInformationException) { }
        return (rx, tx);
    }

    private static IPInterfaceProperties? SafeProps(NetworkInterface nic)
    {
        try { return nic.GetIPProperties(); }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException) { return null; }
    }
}
