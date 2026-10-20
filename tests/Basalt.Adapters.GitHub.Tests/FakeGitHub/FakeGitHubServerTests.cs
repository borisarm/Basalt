using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Basalt.Testing.FakeGitHub;

namespace Basalt.Adapters.GitHub.Tests.FakeGitHub;

public sealed class FakeGitHubServerTests
{
    private static readonly string[] Files = ["a.txt", "foo.bar", "foo/bar.txt"];

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task Tree_And_Commit_Sha_Match_Git()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);
        server.CreateRepo("acme", "vault");
        await InitializeAsync(client);

        var tree = await PostJsonAsync(client, "/repos/acme/vault/git/trees", new
        {
            tree = new object[]
            {
                new { path = "a.txt", mode = "100644", type = "blob", content = "hello\n" },
                new { path = "foo.bar", mode = "100644", type = "blob", content = "y\n" },
                new { path = "foo/bar.txt", mode = "100644", type = "blob", content = "x\n" },
            },
        });
        tree["sha"]!.GetValue<string>().Should().Be("12899b92c74c0c98363a7290dc64c358954744a6");

        var commit = await PostJsonAsync(client, "/repos/acme/vault/git/commits", new
        {
            message = "Initial sync",
            tree = tree["sha"]!.GetValue<string>(),
            author = new { name = "Basalt", email = "basalt@example.com", date = "2026-10-19T12:34:56+02:00" },
            committer = new { name = "Basalt", email = "basalt@example.com", date = "2026-10-19T12:34:56+02:00" },
        });

        commit["sha"]!.GetValue<string>().Should().Be("8b5c2337fa39e475d69e8160144d8c05fb22cf4f");
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task EmptyRepository_Returns409_UntilTheFirstContentsPut()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);
        server.CreateRepo("acme", "empty");

        (await client.GetAsync("/repos/acme/empty/git/ref/heads/main", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var response = await client.PutAsJsonAsync("/repos/acme/empty/contents/.basalt/vault.json", new
        {
            message = "Vault metadata",
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"id\":\"v1\"}")),
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var reference = await client.GetAsync("/repos/acme/empty/git/ref/heads/main", Ct);
        reference.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await reference.Content.ReadAsStringAsync(Ct))!;
        body["object"]!["sha"]!.GetValue<string>().Should().Be(server.GetRepo("acme", "empty").Head());
        server.GetFiles("acme", "empty").Should().ContainKey(".basalt/vault.json");
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task Refs_RejectNonFastForward_And_RaceHookWins()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);
        var repository = server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "one" });
        var first = repository.Head()!;
        var second = server.CommitFiles("acme", "vault", "main", new Dictionary<string, string> { ["a.md"] = "two" });

        (await PatchRefAsync(client, second, force: false)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var rejected = await PatchRefAsync(client, first, force: false);
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await PatchRefAsync(client, first, force: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var raced = false;
        server.BeforeUpdateRef(_ =>
        {
            raced = true;
            server.CommitFiles("acme", "vault", "main", new Dictionary<string, string> { ["b.md"] = "other device" });
        });

        using var result = await PatchRefAsync(client, first, force: false);
        result.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        raced.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task RefETag_Returns304_WithoutConsumingRateLimit()
    {
        await using var server = await FakeGitHubServer.StartAsync(new FakeGitHubOptions { RateLimit = 2 }, Ct);
        using var client = server.CreateClient(server.Options.Token);
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "one" });

        using var first = await client.GetAsync("/repos/acme/vault/git/ref/heads/main", Ct);
        first.Headers.GetValues("x-ratelimit-remaining").Single().Should().Be("1");
        var etag = first.Headers.ETag!.Tag;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/repos/acme/vault/git/ref/heads/main");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var notModified = await client.SendAsync(request, Ct);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);

        using var third = await client.GetAsync("/repos/acme/vault/git/ref/heads/main", Ct);
        third.StatusCode.Should().Be(HttpStatusCode.OK);
        third.Headers.GetValues("x-ratelimit-remaining").Single().Should().Be("0");

        using var exhausted = await client.GetAsync("/repos/acme/vault/git/ref/heads/main", Ct);
        exhausted.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        exhausted.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task RecursiveTree_IsTruncatedAtTheConfiguredThreshold()
    {
        await using var server = await FakeGitHubServer.StartAsync(new FakeGitHubOptions { TreeTruncationThreshold = 2 }, Ct);
        using var client = server.CreateClient(server.Options.Token);
        var repository = server.CreateRepo("acme", "vault", new Dictionary<string, string>
        {
            ["a.md"] = "a",
            ["dir/b.md"] = "b",
            ["dir/c.md"] = "c",
        });

        var commit = await GetJsonAsync(client, $"/repos/acme/vault/git/commits/{repository.Head()}");
        var tree = await GetJsonAsync(client, $"/repos/acme/vault/git/trees/{commit["tree"]!["sha"]!.GetValue<string>()}?recursive=1");

        tree["truncated"]!.GetValue<bool>().Should().BeTrue();
        tree["tree"]!.AsArray().Count.Should().Be(2);

        var root = await GetJsonAsync(client, $"/repos/acme/vault/git/trees/{commit["tree"]!["sha"]!.GetValue<string>()}");
        root["truncated"]!.GetValue<bool>().Should().BeFalse();
        root["tree"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>())
            .Should().BeEquivalentTo("a.md", "dir");
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task UserRepos_ArePaginatedWithLink()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);
        server.CreateRepo("acme", "one");
        server.CreateRepo("acme", "two");
        server.CreateRepo("acme", "three");

        using var first = await client.GetAsync("/user/repos?per_page=2", Ct);
        var page = JsonNode.Parse(await first.Content.ReadAsStringAsync(Ct))!.AsArray();
        page.Count.Should().Be(2);
        first.Headers.GetValues("Link").Single().Should().Contain("page=2");

        using var second = await client.GetAsync("/user/repos?per_page=2&page=2", Ct);
        JsonNode.Parse(await second.Content.ReadAsStringAsync(Ct))!.AsArray().Count.Should().Be(1);
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task FaultPlan_InjectsStatusesRetryAfterTruncationAndDrops()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);

        server.Faults.FailNext("GET", "/user", status: 503, retryAfter: TimeSpan.FromSeconds(3));
        using var failed = await client.GetAsync("/user", Ct);
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        failed.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(3));

        using var recovered = await client.GetAsync("/user", Ct);
        recovered.StatusCode.Should().Be(HttpStatusCode.OK);

        server.Faults.TruncateNext("GET", "/user");
        var truncated = await client.GetStringAsync("/user", Ct);
        FluentActions.Invoking(() => JsonNode.Parse(truncated)).Should().Throw<Exception>();

        server.Faults.DropConnectionNext("GET", "/user");
        await FluentActions.Awaiting(() => client.GetAsync("/user", Ct)).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task DeviceFlow_HandlesPendingSlowDownApprovalAndDenial()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient();
        server.DeviceFlow.ApproveAfterPolls = 3;
        server.DeviceFlow.SlowDownPolls.Add(2);

        using var code = await client.PostAsync("/login/device/code", null, Ct);
        var codeJson = JsonNode.Parse(await code.Content.ReadAsStringAsync(Ct))!;
        codeJson["device_code"]!.GetValue<string>().Should().Be(server.DeviceFlow.DeviceCode);

        (await PollAsync(client, "authorization_pending"))["error"]!.GetValue<string>().Should().Be("authorization_pending");
        var slowDown = await PollAsync(client, "slow_down");
        slowDown["error"]!.GetValue<string>().Should().Be("slow_down");
        slowDown["interval"]!.GetValue<int>().Should().Be(10);
        var token = await PollAsync(client, "approved");
        token["access_token"]!.GetValue<string>().Should().Be(server.DeviceFlow.AccessToken);

        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/user");
        authorized.Headers.Authorization = new("Bearer", server.DeviceFlow.AccessToken);
        (await client.SendAsync(authorized, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        server.DeviceFlow.Reset();
        server.DeviceFlow.Outcome = FakeDeviceFlowOutcome.Deny;
        (await PollAsync(client, "denied"))["error"]!.GetValue<string>().Should().Be("access_denied");
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task ControlApi_SimulatesAnotherDevice_AndRenameRedirects()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);
        server.CreateRepo("acme", "vault", new Dictionary<string, string> { ["a.md"] = "one" });

        var sha = server.CommitFiles("acme", "vault", "main", new Dictionary<string, string> { ["b.md"] = "web edit" });
        server.GetRepo("acme", "vault").Head().Should().Be(sha);
        server.GetFiles("acme", "vault").Should().Contain(new KeyValuePair<string, string>("b.md", "web edit"));

        server.ForcePush("acme", "vault", "main", new Dictionary<string, string> { ["c.md"] = "forced" });
        server.GetRepo("acme", "vault").Head().Should().NotBe(sha);

        var current = server.GetRepo("acme", "vault").Head()!;
        server.GetRepo("acme", "vault").Archived = true;
        (await PatchRefAsync(client, current, force: false)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        server.GetRepo("acme", "vault").Archived = false;

        server.RenameRepo("acme", "vault", "renamed");
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(server.BaseAddress),
        };
        noRedirect.DefaultRequestHeaders.Authorization = new("Bearer", server.Options.Token);
        using var moved = await noRedirect.GetAsync("/repos/acme/vault", Ct);
        moved.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        moved.Headers.Location!.ToString().Should().Be("/repos/acme/renamed");

        server.Requests.Should().OnlyContain(request => !request.Query.Contains(server.Options.Token, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task Zipball_RedirectionContainsTheRealFiles()
    {
        await using var server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);
        var repository = server.CreateRepo("acme", "vault", new Dictionary<string, string>
        {
            ["a.md"] = "# A\n",
            ["dir/b.md"] = "B\n",
        });

        using var response = await client.GetAsync("/repos/acme/vault/zipball/main", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(Ct), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        names.Should().Contain($"{repository.Owner}-{repository.Name}-{repository.Head()}/a.md");
        using var reader = new StreamReader(archive.Entries.Single(entry => entry.FullName.EndsWith("dir/b.md", StringComparison.Ordinal)).Open());
        (await reader.ReadToEndAsync(Ct)).Should().Be("B\n");
    }

    [Fact]
    [Trait("Spec", "FAKE-GITHUB")]
    public async Task RunsOnTestServer()
    {
        await using var server = await FakeGitHubServer.StartTestServerAsync(cancellationToken: Ct);
        using var client = server.CreateClient(server.Options.Token);

        using var response = await client.GetAsync("/user", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetJsonAsync(client, "/user"))["login"]!.GetValue<string>().Should().Be(server.Options.Login);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task InitializeAsync(HttpClient client)
    {
        using var response = await client.PutAsJsonAsync("/repos/acme/vault/contents/.basalt/vault.json", new
        {
            message = "Vault metadata",
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"id\":\"v1\"}")),
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<JsonNode> PostJsonAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<HttpResponseMessage> PatchRefAsync(HttpClient client, string sha, bool force)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/repos/acme/vault/git/refs/heads/main")
        {
            Content = JsonContent.Create(new { sha, force }),
        };
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> PollAsync(HttpClient client, string scenario)
    {
        using var response = await client.PostAsync("/login/oauth/access_token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = "basalt",
                ["device_code"] = "device-code",
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["scenario"] = scenario,
            }), Ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }
}
