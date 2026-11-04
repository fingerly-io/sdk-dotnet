using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Fingerly.AspNetCore.Tests;

/// <summary>
/// Sends a POST with a request target exactly as given. HttpClient resolves
/// dot segments and escapes before sending, which would hide what a browser
/// or an attacker can put on the wire.
/// </summary>
internal static class RawPost
{
    internal static async Task<int> StatusAsync(Uri origin, string target, string publicKey)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(origin.Host, origin.Port);
        using NetworkStream stream = tcp.GetStream();
        string request = "POST " + target + " HTTP/1.1\r\n"
            + "Host: " + origin.Authority + "\r\n"
            + "X-API-Key: " + publicKey + "\r\n"
            + "Content-Type: application/json\r\n"
            + "Content-Length: 2\r\n"
            + "Connection: close\r\n\r\n{}";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        string? statusLine = await reader.ReadLineAsync();
        return int.Parse(statusLine!.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }
}
