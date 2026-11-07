using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;
using redb.Route.Firebase;
using Xunit.Abstractions;

namespace redb.Route.Tests.Firebase.Integration;

/// <summary>
/// The shipped <see cref="FirebaseCredentialProvider"/> against the live containers
/// (docker-compose.tests.yml: <c>firebase-emulators</c> :8086, <c>firebase-gcs</c> :4443).
/// Wave А2/А3 of docs/V4/11-FIREBASE.md: the production connect path must reach the
/// emulators via env vars on its own — no hand-built clients, no test-only providers.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FirebaseEnvSensitive")]
public sealed class ProductionCredentialProviderTests : IDisposable
{
    private const string ProjectId = "demo-redb";
    private const string FirestoreHost = "localhost:8086";
    // Google.Cloud.Storage.V1 uses STORAGE_EMULATOR_HOST verbatim as the service base URI,
    // so for fake-gcs-server it must carry the full /storage/v1/ path.
    private const string GcsEmulatorBaseUri = "http://localhost:4443/storage/v1/";
    private const string TestBucket = "prod-path-bucket";

    private readonly ITestOutputHelper _output;
    private readonly string? _prevFirestoreEmu;
    private readonly string? _prevStorageEmu;
    private readonly string? _prevGac;

    public ProductionCredentialProviderTests(ITestOutputHelper output)
    {
        _output = output;
        _prevFirestoreEmu = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
        _prevStorageEmu = Environment.GetEnvironmentVariable("STORAGE_EMULATOR_HOST");
        _prevGac = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        // The emulator path must not depend on real credentials being around.
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", _prevFirestoreEmu);
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", _prevStorageEmu);
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", _prevGac);
    }

    [Fact]
    public async Task Firestore_ProductionProvider_DetectsEmulator()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", FirestoreHost);
        using var provider = new FirebaseCredentialProvider();

        var db = provider.GetFirestoreDb(ProjectId);

        var docRef = db.Collection($"prodpath-{Guid.NewGuid():N}").Document("doc1");
        await docRef.SetAsync(new Dictionary<string, object?> { ["ok"] = true });
        var snapshot = await docRef.GetSnapshotAsync();

        snapshot.Exists.Should().BeTrue("продовый провайдер обязан сам находить эмулятор по FIRESTORE_EMULATOR_HOST");
        snapshot.GetValue<bool>("ok").Should().BeTrue();
        _output.WriteLine($"Firestore prod path OK: {docRef.Path}");
    }

    [Fact]
    public async Task Storage_ProductionProvider_DetectsEmulator()
    {
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", GcsEmulatorBaseUri);
        using var provider = new FirebaseCredentialProvider();

        var client = provider.GetStorageClient();

        try
        {
            await client.CreateBucketAsync(ProjectId, TestBucket);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // already exists
        }

        var name = $"prodpath-{Guid.NewGuid():N}.txt";
        using (var data = new MemoryStream(Encoding.UTF8.GetBytes("prod-path")))
            await client.UploadObjectAsync(TestBucket, name, "text/plain", data);

        using var download = new MemoryStream();
        await client.DownloadObjectAsync(TestBucket, name, download);
        Encoding.UTF8.GetString(download.ToArray()).Should().Be("prod-path",
            "продовый провайдер обязан сам находить эмулятор по STORAGE_EMULATOR_HOST");
        _output.WriteLine($"Storage prod path OK: {TestBucket}/{name}");
    }

    [Fact]
    public void GetFirestoreDb_ConsumesCredentialPath_FailsLoudOnMissingFile()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        using var provider = new FirebaseCredentialProvider();
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-sa-{Guid.NewGuid():N}.json");

        var act = () => provider.GetFirestoreDb("some-project", missing);

        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain(Path.GetFileName(missing),
                "credentialPath обязан реально использоваться, а не молча падать в ADC");
    }

    [Fact]
    public void GetStorageClient_ConsumesCredentialPath_FailsLoudOnMissingFile()
    {
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", null);
        using var provider = new FirebaseCredentialProvider();
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-sa-{Guid.NewGuid():N}.json");

        var act = () => provider.GetStorageClient(missing);

        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain(Path.GetFileName(missing),
                "credentialPath обязан реально использоваться, а не молча падать в ADC");
    }

    [Fact]
    public void GetFirestoreDb_CachesPerProject_NotFirstCallerWins()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", FirestoreHost);
        using var provider = new FirebaseCredentialProvider();

        var dbA = provider.GetFirestoreDb("proj-a");
        var dbB = provider.GetFirestoreDb("proj-b");
        var dbA2 = provider.GetFirestoreDb("proj-a");

        dbA.ProjectId.Should().Be("proj-a");
        dbB.ProjectId.Should().Be("proj-b",
            "второй проект не имеет права молча получить базу первого");
        dbA2.Should().BeSameAs(dbA, "клиент кэшируется по projectId");
    }

    // ── Г2: named FirebaseApp — без процесс-глобального DEFAULT ──

    [Fact]
    public void GetOrCreateApp_TwoProviders_SameArgs_NoCollision()
    {
        var sa = WriteFakeServiceAccount("unit-proj");
        using var p1 = new FirebaseCredentialProvider();
        using var p2 = new FirebaseCredentialProvider();

        var app1 = p1.GetOrCreateApp(sa, "unit-proj");
        var act = () => p2.GetOrCreateApp(sa, "unit-proj");

        act.Should().NotThrow("второй провайдер (registry-сценарий) не имеет права падать о процесс-глобальный app");
        app1.Should().NotBeNull();
    }

    [Fact]
    public void GetOrCreateApp_DifferentCredentialPaths_BothServed()
    {
        var sa1 = WriteFakeServiceAccount("proj-one");
        var sa2 = WriteFakeServiceAccount("proj-two");
        using var provider = new FirebaseCredentialProvider();

        var app1 = provider.GetOrCreateApp(sa1, "proj-one");
        var app2 = provider.GetOrCreateApp(sa2, "proj-two");

        app1.Should().NotBeNull();
        app2.Should().NotBeNull();
        app2.Should().NotBeSameAs(app1, "два сервис-аккаунта = два независимых app");
    }

    [Fact]
    public void GetOrCreateApp_LeavesDefaultInstanceAlone()
    {
        var sa = WriteFakeServiceAccount("unit-proj");
        using var provider = new FirebaseCredentialProvider();

        provider.GetOrCreateApp(sa, "unit-proj");

        FirebaseAdmin.FirebaseApp.DefaultInstance.Should().BeNull(
            "коннектор не имеет права занимать процесс-глобальный DEFAULT app хоста");
    }

    // ── Г3: проект обязателен — никакого молчаливого default-project ──

    [Fact]
    public void GetFirestoreDb_NoProjectAnywhere_FailsWithGuidance()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        var prevProject = Environment.GetEnvironmentVariable("FIREBASE_PROJECT");
        Environment.SetEnvironmentVariable("FIREBASE_PROJECT", null);
        try
        {
            using var provider = new FirebaseCredentialProvider();
            var act = () => provider.GetFirestoreDb();

            act.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().Contain("FIREBASE_PROJECT",
                    "отсутствие проекта — громкая ошибка с подсказкой, а не молчаливый default-project");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FIREBASE_PROJECT", prevProject);
        }
    }

    // ── Helpers ──

    /// <summary>Writes a syntactically valid service-account JSON with a freshly generated RSA key.</summary>
    private static string WriteFakeServiceAccount(string projectId)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var b64 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        var pemBody = string.Join("\n", System.Text.RegularExpressions.Regex
            .Matches(b64, ".{1,64}").Select(m => m.Value));
        var pem = $"-----BEGIN PRIVATE KEY-----\n{pemBody}\n-----END PRIVATE KEY-----\n";

        var json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = projectId,
            private_key_id = "fake-key-id",
            private_key = pem,
            client_email = $"unit@{projectId}.iam.gserviceaccount.com",
            client_id = "0",
            token_uri = "https://oauth2.googleapis.com/token",
        });

        var path = Path.Combine(Path.GetTempPath(), $"fake-sa-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
