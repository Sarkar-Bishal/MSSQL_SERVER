using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MSSQL_Server.Core
{
    /// <summary>Small helper that reports the device's LAN IPv4 addresses (for PC SSMS / PC clients).</summary>
    public static class NetworkInfo
    {
        public static List<string> GetActiveIPv4Addresses()
        {
            var addresses = new List<string>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(unicast.Address)
                            && !addresses.Contains(unicast.Address.ToString()))
                        {
                            addresses.Add(unicast.Address.ToString());
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Network state is not always readable; an empty list is fine.
            }

            return addresses;
        }
    }
}
