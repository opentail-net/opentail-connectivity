using System.Net;
using OpenTail.Connectivity.Social.Meta;
using OpenTail.Connectivity.Social.Shared;

namespace OpenTail.Connectivity.Tests.Social;

/// <summary>A caller's shared HttpClient never carries one account's token to another account's requests.</summary>
public sealed class MetaSharedClientTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<string?> Tokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Tokens) Tokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") });
        }
    }

    [Fact]
    public async Task Two_connectors_on_one_client_each_send_their_own_token()
    {
        var recorder = new Recorder();
        var shared = new HttpClient(recorder);
        using var first = new MetaConnector("token-first", "page:1", new SocialConnectorOptions(TenantContext: "t"), shared);
        using var second = new MetaConnector("token-second", "page:2", new SocialConnectorOptions(TenantContext: "t"), shared);

        await foreach (var _ in first.DiscoverAsync(TestContext.Current.CancellationToken)) { }
        await foreach (var _ in second.DiscoverAsync(TestContext.Current.CancellationToken)) { }

        Assert.Equal(["token-first", "token-second"], recorder.Tokens);
        Assert.Null(shared.DefaultRequestHeaders.Authorization);
    }
}
