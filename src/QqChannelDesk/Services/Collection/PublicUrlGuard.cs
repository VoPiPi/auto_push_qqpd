using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;

namespace QqChannelDesk.Services.Collection;

public static class PublicUrlGuard
{
    public static bool TryNormalize(string? input, out Uri uri, out string error) =>
        PublicArticleFetcher.TryNormalizePublicUrl(input ?? "", out uri, out error);

    public static HttpMessageHandler CreatePublicHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = ConnectPublicAsync
        };
    }

    public static HttpClient CreateHttpClient() =>
        new(CreatePublicHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        PublicArticleFetcher.EnsurePublicAddress(addresses);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = exception;
                if (exception is OperationCanceledException) throw;
            }
        }

        throw new HttpRequestException("无法连接到公开内容服务器。", lastError);
    }
}
