using System.Net;

namespace AutoEnvPlus.Core.Tests;

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpResponseMessage response = responder(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Text(string value, string mediaType = "text/plain") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, System.Text.Encoding.UTF8, mediaType),
        };

    public static HttpResponseMessage Bytes(byte[] value) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(value),
        };
}
