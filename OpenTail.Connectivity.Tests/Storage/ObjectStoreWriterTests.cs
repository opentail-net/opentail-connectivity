using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.S3;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class ObjectStoreWriterTests
{
    [Fact]
    public async Task Guard_refuses_keys_outside_prefixes_or_with_traversal_segments()
    {
        var client = new InMemoryObjectStoreClient("bucket");
        var guard = new ObjectStoreWriterGuard(new InMemoryObjectStoreWriter(client), ["allowed/"], 100);

        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => guard.PutAsync("other/a.txt", Content("x")));
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => guard.PutAsync("allowed/../secret.txt", Content("x")));
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => guard.PutAsync("/allowed/a.txt", Content("x")));
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => guard.DeleteAsync("other/a.txt"));
    }

    [Fact]
    public async Task Guard_counts_stream_bytes_and_refuses_oversize_content_before_writing()
    {
        var client = new InMemoryObjectStoreClient("bucket");
        var guard = new ObjectStoreWriterGuard(new InMemoryObjectStoreWriter(client), ["allowed/"], 3);

        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => guard.PutAsync("allowed/a.txt", Content("1234")));
        Assert.Null(await client.HeadAsync("allowed/a.txt"));
    }

    [Fact]
    public async Task In_memory_writer_round_trips_overwrites_and_deletes()
    {
        var client = new InMemoryObjectStoreClient("bucket");
        var writer = new InMemoryObjectStoreWriter(client);

        var written = await writer.PutAsync("docs/a.txt", Content("first"), "text/plain");
        var read = await client.ReadAsync("docs/a.txt", 100);
        Assert.Equal(5, written.Size);
        Assert.Equal("text/plain", read.ContentType);
        Assert.Equal("first", Encoding.UTF8.GetString(read.Bytes));
        await Assert.ThrowsAsync<ObjectExistsException>(() => writer.PutAsync("docs/a.txt", Content("second")));

        await writer.PutAsync("docs/a.txt", Content("second"), overwrite: true);
        Assert.Equal("second", Encoding.UTF8.GetString((await client.ReadAsync("docs/a.txt", 100)).Bytes));
        Assert.True(await writer.DeleteAsync("docs/a.txt"));
        Assert.False(await writer.DeleteAsync("docs/a.txt"));
    }

    [Fact]
    public async Task S3_writer_sends_bucket_key_content_type_and_conditional_create_header()
    {
        var (writer, handler) = CreateS3(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        });

        var result = await writer.PutAsync("docs/a.txt", Content("hello"), "text/plain");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/bucket/docs/a.txt", request.Uri.AbsolutePath);
        Assert.Equal("text/plain", request.ContentType);
        Assert.Equal("*", request.IfNoneMatch);
        Assert.Equal(5, result.Size);
    }

    [Fact]
    public async Task S3_writer_maps_precondition_failure_to_object_exists()
    {
        var (writer, _) = CreateS3(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent("<Error><Code>PreconditionFailed</Code></Error>", Encoding.UTF8, "application/xml"),
        });

        var error = await Assert.ThrowsAsync<ObjectExistsException>(() => writer.PutAsync("docs/a.txt", Content("hello")));

        Assert.Equal("docs/a.txt", error.Key);
    }

    [Fact]
    public async Task S3_writer_deletes_only_existing_objects()
    {
        var (writer, handler) = CreateS3(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }
            : new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.True(await writer.DeleteAsync("docs/a.txt"));
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Delete }, handler.Requests.Select(r => r.Method));

        var (missingWriter, _) = CreateS3(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<Error><Code>NoSuchKey</Code></Error>", Encoding.UTF8, "application/xml"),
        });
        Assert.False(await missingWriter.DeleteAsync("docs/missing.txt"));
    }

    [Fact]
    public void Readers_and_writers_expose_only_their_own_direction()
    {
        var readerMethods = typeof(S3ObjectStoreClient)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(method => method.DeclaringType == typeof(S3ObjectStoreClient))
            .Select(method => method.Name);
        var writerMethods = typeof(S3ObjectStoreWriter)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(method => method.DeclaringType == typeof(S3ObjectStoreWriter))
            .Select(method => method.Name);

        Assert.DoesNotContain(readerMethods, name => name is "PutAsync" or "DeleteAsync");
        Assert.DoesNotContain(writerMethods, name => name is "ListAsync" or "ReadAsync" or "HeadAsync");
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    private static (S3ObjectStoreWriter Writer, StubHandler Handler) CreateS3(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var sdk = S3ObjectStoreClient.CreateSdkClient(
            new HttpClient(handler),
            new AmazonS3Config { ServiceURL = "https://s3.example.test", ForcePathStyle = true, AuthenticationRegion = "eu-west-1" },
            new BasicAWSCredentials("AKIDEXAMPLE", "secret-example"));
        return (new S3ObjectStoreWriter(sdk, "bucket"), handler);
    }

    private sealed record SeenRequest(HttpMethod Method, Uri Uri, string? ContentType, string? IfNoneMatch);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType,
                request.Headers.IfNoneMatch.FirstOrDefault()?.Tag));
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
