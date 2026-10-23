using Amazon.Runtime;
using Amazon.S3;
using OpenTail.Connectivity.Drive.Google;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.Box;
using OpenTail.Connectivity.Storage.Dropbox;
using OpenTail.Connectivity.Storage.S3;
using OpenTail.Connectivity.Storage.Sftp;

namespace OpenTail.Connectivity.Tests.Storage;

/// <summary>
/// The identity class test (plan section 31): two accounts configured the same way, in the same tenant, never share a container,
/// item ids or a sync scope. A new storage or drive connector gets a case here.
/// </summary>
public sealed class AccountIdentityTests
{
    private const string Tenant = "acme";
    private const string Key = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static void Distinct(IObjectStoreClient first, IObjectStoreClient second)
    {
        Assert.NotEqual(first.Container, second.Container);
        Assert.NotEqual(new ObjectStorageConnector(first, Tenant).SyncScope, new ObjectStorageConnector(second, Tenant).SyncScope);
    }

    [Fact]
    public void Box_root_folders_of_two_accounts()
    {
        using var alice = new BoxObjectStoreClient("t1", "0", accountNamespace: "1001");
        using var bob = new BoxObjectStoreClient("t2", "0", accountNamespace: "1002");
        Distinct(alice, bob);
    }

    [Fact]
    public void Box_refuses_the_root_folder_without_an_account()
        => Assert.Throws<ArgumentException>(() => new BoxObjectStoreClient("t", "0"));

    [Fact]
    public void Sftp_users_of_one_server_and_root()
    {
        using var alice = new SftpObjectStoreClient(new SftpSettings("files.example", 22, "alice", Key, "/"), password: "pw");
        using var bob = new SftpObjectStoreClient(new SftpSettings("files.example", 22, "bob", Key, "/"), password: "pw");
        using var otherPort = new SftpObjectStoreClient(new SftpSettings("files.example", 2222, "alice", Key, "/"), password: "pw");
        Distinct(alice, bob);
        Distinct(alice, otherPort);
    }

    [Fact]
    public void S3_compatible_endpoints_with_the_same_bucket()
    {
        static S3ObjectStoreClient On(string endpoint) => new(S3ObjectStoreClient.CreateSdkClient(new HttpClient(),
            new AmazonS3Config { ServiceURL = endpoint, AuthenticationRegion = "eu-west-1" }, new BasicAWSCredentials("a", "b")), "docs");
        Distinct(On("https://minio.one:9000"), On("https://minio.two:9000"));
    }

    [Fact]
    public void Dropbox_accounts_with_the_same_folder()
    {
        var client = new DropboxObjectStoreClient(_ => Task.FromResult("t"), "/reports");
        Assert.NotEqual(new DropboxConnector(client, Tenant, "dbid:alice").SyncScope, new DropboxConnector(client, Tenant, "dbid:bob").SyncScope);
    }

    [Fact]
    public void Google_my_drives_named_by_account()
    {
        static GoogleDriveConnector For(string? account) =>
            new(_ => Task.FromResult("t"), new GoogleDriveTarget.MyDrive(account), Tenant, null, new HttpClient(), null);
        Assert.NotEqual(For("alice@corp.example").SyncScope, For("bob@corp.example").SyncScope);
        Assert.Equal(For("Alice@Corp.example").SyncScope, For("alice@corp.example").SyncScope);
    }
}
