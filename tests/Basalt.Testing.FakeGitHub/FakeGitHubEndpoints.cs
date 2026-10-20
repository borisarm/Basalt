using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Basalt.Domain.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Basalt.Testing.FakeGitHub;

internal static class FakeGitHubEndpoints
{
    private const string JsonContentType = "application/json";

    public static void Map(WebApplication app, FakeGitHubServer server)
    {
        app.Use(async (context, next) =>
        {
            server.Record(new RequestRecord(
                DateTimeOffset.UtcNow,
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Request.QueryString.Value ?? string.Empty,
                context.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase)));

            if (!IsPublicPath(context.Request.Path) && !server.IsAuthorized(context.Request))
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Bad credentials").ConfigureAwait(false);
                return;
            }

            var countsForRateLimit = !IsPublicPath(context.Request.Path) && server.Options.RateLimit > 0;
            if (countsForRateLimit)
            {
                var remaining = Interlocked.Decrement(ref server.RateRemaining);
                WriteRateLimitHeaders(context, remaining, server.Options);
                if (remaining < 0)
                {
                    var resetIn = Math.Max(1, (int)(server.Options.RateLimitReset - DateTimeOffset.UtcNow).TotalSeconds);
                    context.Response.Headers["Retry-After"] = resetIn.ToString(CultureInfo.InvariantCulture);
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "API rate limit exceeded").ConfigureAwait(false);
                    return;
                }
            }

            var fault = server.Faults.Match(context.Request.Method, context.Request.Path.Value ?? string.Empty);
            if (fault is not null)
            {
                if (fault.Delay is { } delay)
                {
                    await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
                }

                if (fault.Kind == FaultKind.DropConnection)
                {
                    context.Abort();
                    return;
                }

                if (fault.Kind == FaultKind.Truncate)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = JsonContentType;
                    await context.Response.WriteAsync("{", context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                if (fault.RetryAfter is { } retryAfter)
                {
                    context.Response.Headers["Retry-After"] = ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await WriteErrorAsync(context, fault.Status, "Injected fault").ConfigureAwait(false);
                return;
            }

            var redirect = server.RedirectFor(context.Request.Path.Value ?? string.Empty);
            if (redirect is not null)
            {
                var segments = (context.Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
                var rest = segments.Length > 3 ? "/" + string.Join('/', segments.Skip(3)) : string.Empty;
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                context.Response.Headers["Location"] = $"/repos/{redirect}{rest}{context.Request.QueryString.Value}";
                return;
            }

            await next(context).ConfigureAwait(false);
            if (context.Response.StatusCode == StatusCodes.Status304NotModified && countsForRateLimit)
            {
                Interlocked.Increment(ref server.RateRemaining);
            }
        });

        MapAuth(app, server);
        MapRepositories(app, server);
        MapGitData(app, server);
        MapContents(app, server);
        MapDownloads(app, server);
    }

    private static void MapAuth(WebApplication app, FakeGitHubServer server)
    {
        app.MapGet("/user", () => Results.Json(new
        {
            login = server.Options.Login,
            id = server.Options.UserId,
            node_id = "U_" + server.Options.UserId,
            avatar_url = server.Options.AvatarUrl,
            name = server.Options.UserName,
            type = "User",
        }));

        app.MapPost("/login/device/code", () => Results.Json(new
        {
            device_code = server.DeviceFlow.DeviceCode,
            user_code = server.DeviceFlow.UserCode,
            verification_uri = server.DeviceFlow.VerificationUri,
            expires_in = server.DeviceFlow.ExpiresInSeconds,
            interval = server.DeviceFlow.IntervalSeconds,
        }));

        app.MapPost("/login/oauth/access_token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            if (form["device_code"].ToString() != server.DeviceFlow.DeviceCode)
            {
                return Results.Json(new { error = "incorrect_device_code" });
            }

            var answer = server.DeviceFlow.Poll();
            if (answer.Error is not null)
            {
                return Results.Json(answer.Interval is { } interval
                    ? new { error = answer.Error, interval }
                    : new { error = answer.Error });
            }

            server.IssueToken(answer.Token!);
            return Results.Json(new { access_token = answer.Token, token_type = "bearer", scope = answer.Scope });
        });

        app.MapGet("/rate_limit", () => Results.Json(new
        {
            resources = new
            {
                core = new
                {
                    limit = server.Options.RateLimit,
                    remaining = Math.Max(0, server.RateRemaining),
                    reset = server.Options.RateLimitReset.ToUnixTimeSeconds(),
                },
            },
        }));
    }

    private static void MapRepositories(WebApplication app, FakeGitHubServer server)
    {
        app.MapGet("/user/repos", (HttpContext context) =>
        {
            var repositories = server.Repositories.OrderBy(repository => repository.Id).ToList();
            var (items, next) = Page(context, repositories, server.Options);
            SetLink(context, next);
            return Results.Json(items.Select(repository => RepoDto(context, repository)));
        });

        app.MapPost("/user/repos", async (HttpContext context) =>
        {
            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            return CreateRepository(context, server, body, server.Options.Login);
        });

        app.MapPost("/orgs/{org}/repos", async (HttpContext context, string org) =>
        {
            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            return CreateRepository(context, server, body, org);
        });

        app.MapGet("/repos/{owner}/{repo}", (HttpContext context, string owner, string repo) =>
        {
            var repository = server.FindRepo(owner, repo);
            return repository is null
                ? NotFound()
                : Results.Json(RepoDto(context, repository));
        });
    }

    private static void MapGitData(WebApplication app, FakeGitHubServer server)
    {
        app.MapGet("/repos/{owner}/{repo}/git/ref/{**refPath}", (HttpContext context, string owner, string repo, string? refPath) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var branch = Branch(refPath);
            var head = repository.Head(branch);
            if (head is null)
            {
                return NotFound();
            }

            var etag = "\"" + head + "\"";
            if (context.Request.Headers["If-None-Match"].Contains(etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            context.Response.Headers["ETag"] = etag;
            return Results.Json(RefDto(context, owner, repo, branch, head));
        });

        app.MapGet("/repos/{owner}/{repo}/git/refs/{**refPath}", (HttpContext context, string owner, string repo, string? refPath) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var branch = Branch(refPath);
            var head = repository.Head(branch);
            if (head is null)
            {
                return NotFound();
            }

            var etag = "\"" + head + "\"";
            if (context.Request.Headers["If-None-Match"].Contains(etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            context.Response.Headers["ETag"] = etag;
            return Results.Json(RefDto(context, owner, repo, branch, head));
        });

        app.MapPost("/repos/{owner}/{repo}/git/refs", async (HttpContext context, string owner, string repo) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (!repository.AllowPush || repository.Archived)
            {
                return Forbidden();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var fullRef = Str(body, "ref");
            var sha = Str(body, "sha");
            if (fullRef is null || sha is null || FakeGitStore.FindCommit(repository, sha) is null)
            {
                return ValidationFailed("Invalid ref.");
            }

            var branch = Branch(fullRef);
            if (repository.Head(branch) is not null)
            {
                return ValidationFailed("Reference already exists");
            }

            FakeGitStore.SetRef(repository, branch, sha);
            return Results.Json(RefDto(context, owner, repo, branch, sha), statusCode: StatusCodes.Status201Created);
        });

        app.MapPatch("/repos/{owner}/{repo}/git/refs/{**refPath}", async (HttpContext context, string owner, string repo, string? refPath) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (!repository.AllowPush || repository.Archived)
            {
                return Forbidden();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var sha = Str(body, "sha");
            var force = Bool(body, "force") ?? false;
            if (sha is null || FakeGitStore.FindCommit(repository, sha) is null)
            {
                return ValidationFailed("Invalid commit.");
            }

            var branch = Branch(refPath);
            await server.InvokeBeforeUpdateRefAsync(repository).ConfigureAwait(false);
            var current = repository.Head(branch);
            if (current is null)
            {
                return NotFound();
            }

            if (!force && !FakeGitStore.IsAncestor(repository, current, sha))
            {
                return Results.Json(new
                {
                    message = "Update is not a fast forward",
                    documentation_url = "https://docs.github.com/rest/git/refs#update-a-reference",
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            FakeGitStore.SetRef(repository, branch, sha);
            return Results.Json(RefDto(context, owner, repo, branch, sha));
        });

        app.MapGet("/repos/{owner}/{repo}/git/commits/{sha}", (HttpContext context, string owner, string repo, string sha) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var commit = FakeGitStore.FindCommit(repository, sha);
            return commit is null ? NotFound() : Results.Json(CommitDto(context, owner, repo, commit));
        });

        app.MapPost("/repos/{owner}/{repo}/git/commits", async (HttpContext context, string owner, string repo) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (!repository.AllowPush || repository.Archived)
            {
                return Forbidden();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var tree = Str(body, "tree");
            if (tree is null || FakeGitStore.FindTree(repository, tree) is null)
            {
                return ValidationFailed("Tree not found");
            }

            var parents = new List<CommitSha>();
            if (body?["parents"] is JsonArray parentNodes)
            {
                foreach (var parent in parentNodes)
                {
                    var parentSha = parent?.GetValue<string>();
                    if (parentSha is null || FakeGitStore.FindCommit(repository, parentSha) is null)
                    {
                        return ValidationFailed("Parent not found");
                    }

                    parents.Add(CommitSha.Parse(parentSha).Value);
                }
            }

            var message = Str(body, "message") ?? string.Empty;
            var author = Signature(body?["author"], server.Options.DefaultSignature);
            var committer = Signature(body?["committer"], author);
            var commit = FakeGitStore.CreateCommit(
                repository, TreeSha.Parse(tree).Value, parents, message, author, committer);
            return Results.Json(
                CommitDto(context, owner, repo, FakeGitStore.FindCommit(repository, commit.ToString())!),
                statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/repos/{owner}/{repo}/git/trees/{sha}", (HttpContext context, string owner, string repo, string sha) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var commit = FakeGitStore.FindCommit(repository, sha);
            var treeSha = commit?.Tree ?? sha;
            if (FakeGitStore.FindTree(repository, treeSha) is null)
            {
                return NotFound();
            }

            var recursive = context.Request.Query["recursive"].ToString() is "1" or "true";
            var entries = FakeGitStore.ListTree(
                repository, treeSha, recursive, server.Options.TreeTruncationThreshold, out var truncated);
            return Results.Json(new
            {
                sha = treeSha,
                url = Url(context, $"/repos/{owner}/{repo}/git/trees/{treeSha}"),
                truncated,
                tree = entries.Select(entry => new
                {
                    path = entry.Path,
                    mode = entry.Mode,
                    type = entry.Type,
                    sha = entry.Sha,
                    size = entry.Size,
                    url = Url(context, $"/repos/{owner}/{repo}/git/{entry.Type}s/{entry.Sha}"),
                }),
            });
        });

        app.MapPost("/repos/{owner}/{repo}/git/trees", async (HttpContext context, string owner, string repo) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (!repository.AllowPush || repository.Archived)
            {
                return Forbidden();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var baseTree = Str(body, "base_tree");
            if (baseTree is not null)
            {
                if (FakeGitStore.FindTree(repository, baseTree) is null)
                {
                    return ValidationFailed("Base tree not found");
                }

                foreach (var (path, content) in FakeGitStore.Flatten(repository, baseTree))
                {
                    files[path] = content;
                }
            }

            if (body?["tree"] is not JsonArray entries)
            {
                return ValidationFailed("tree is required");
            }

            foreach (var entry in entries)
            {
                var path = Str(entry, "path");
                if (path is null)
                {
                    return ValidationFailed("path is required");
                }

                path = FakeGitStore.NormalizePath(path);
                var sha = entry?["sha"]?.GetValue<string>();
                if (entry is JsonObject objectEntry && objectEntry.ContainsKey("sha") && objectEntry["sha"] is null)
                {
                    RemovePrefix(files, path);
                    continue;
                }

                if (string.Equals(Str(entry, "type"), "tree", StringComparison.Ordinal))
                {
                    RemovePrefix(files, path);
                    if (sha is not null)
                    {
                        foreach (var (child, content) in FakeGitStore.Flatten(repository, sha))
                        {
                            files[path + "/" + child] = content;
                        }
                    }

                    continue;
                }

                if (entry?["content"] is JsonNode contentNode)
                {
                    files[path] = Encoding.UTF8.GetBytes(contentNode.GetValue<string>());
                }
                else if (sha is not null && FakeGitStore.FindBlob(repository, sha) is { } blob)
                {
                    files[path] = blob;
                }
                else
                {
                    return ValidationFailed($"Blob {sha} not found");
                }
            }

            var tree = FakeGitStore.PutFiles(repository, files);
            var lines = FakeGitStore.ListTree(repository, tree.ToString(), false, int.MaxValue, out _);
            return Results.Json(new
            {
                sha = tree.ToString(),
                url = Url(context, $"/repos/{owner}/{repo}/git/trees/{tree}"),
                tree = lines.Select(line => new
                {
                    path = line.Path,
                    mode = line.Mode,
                    type = line.Type,
                    sha = line.Sha,
                    size = line.Size,
                    url = Url(context, $"/repos/{owner}/{repo}/git/{line.Type}s/{line.Sha}"),
                }),
            }, statusCode: StatusCodes.Status201Created);
        });

        app.MapPost("/repos/{owner}/{repo}/git/blobs", async (HttpContext context, string owner, string repo) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (!repository.AllowPush || repository.Archived)
            {
                return Forbidden();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var encoded = Str(body, "content");
            if (encoded is null)
            {
                return ValidationFailed("content is required");
            }

            var encoding = Str(body, "encoding") ?? "utf-8";
            byte[] content;
            try
            {
                content = encoding == "base64" ? Convert.FromBase64String(encoded) : Encoding.UTF8.GetBytes(encoded);
            }
            catch (FormatException)
            {
                return ValidationFailed("content is not valid base64");
            }

            var sha = FakeGitStore.PutBlob(repository, content);
            return Results.Json(new
            {
                sha = sha.ToString(),
                url = Url(context, $"/repos/{owner}/{repo}/git/blobs/{sha}"),
            }, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/repos/{owner}/{repo}/git/blobs/{sha}", (HttpContext context, string owner, string repo, string sha) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var content = FakeGitStore.FindBlob(repository, sha);
            if (content is null)
            {
                return NotFound();
            }

            if (context.Request.Headers["Accept"].ToString().Contains("raw", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Bytes(content, "application/octet-stream");
            }

            return Results.Json(new
            {
                sha,
                size = content.Length,
                encoding = "base64",
                content = Convert.ToBase64String(content),
            });
        });
    }

    private static void MapContents(WebApplication app, FakeGitHubServer server)
    {
        app.MapGet("/repos/{owner}/{repo}/contents/{**path}", (HttpContext context, string owner, string repo, string? path) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            var reference = context.Request.Query["ref"].ToString();
            var commit = FakeGitStore.ResolveCommit(repository, reference.Length == 0 ? null : reference);
            if (commit is null)
            {
                return NotFound();
            }

            var normalized = path is null ? string.Empty : path.Replace('\\', '/').Trim('/');
            if (normalized.Length > 0)
            {
                var content = FakeGitStore.ReadFile(repository, commit, normalized);
                if (content is not null)
                {
                    var sha = GitBlobHasher.Compute(content).ToString();
                    if (context.Request.Headers["Accept"].ToString().Contains("raw", StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Bytes(content, "application/octet-stream");
                    }

                    return Results.Json(new
                    {
                        name = normalized[(normalized.LastIndexOf('/') + 1)..],
                        path = normalized,
                        sha,
                        size = content.Length,
                        type = "file",
                        encoding = "base64",
                        content = Convert.ToBase64String(content),
                    });
                }
            }

            var commitObject = FakeGitStore.FindCommit(repository, commit);
            if (commitObject is null)
            {
                return NotFound();
            }

            var prefix = normalized.Length == 0 ? string.Empty : normalized + "/";
            var entries = FakeGitStore.ListTree(repository, commitObject.Tree, true, int.MaxValue, out _)
                .Where(entry => entry.Path.StartsWith(prefix, StringComparison.Ordinal)
                    && !entry.Path[prefix.Length..].Contains('/'))
                .Select(entry => new
                {
                    name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..],
                    path = entry.Path,
                    sha = entry.Sha,
                    size = entry.Size,
                    type = entry.Type == "tree" ? "dir" : "file",
                })
                .ToList();
            return entries.Count == 0 ? NotFound() : Results.Json(entries);
        });

        app.MapPut("/repos/{owner}/{repo}/contents/{**path}", async (HttpContext context, string owner, string repo, string? path) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (!repository.AllowPush || repository.Archived)
            {
                return Forbidden();
            }

            if (path is null)
            {
                return ValidationFailed("path is required");
            }

            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var encoded = Str(body, "content");
            if (encoded is null)
            {
                return ValidationFailed("content is required");
            }

            var encoding = Str(body, "encoding") ?? "base64";
            byte[] content;
            try
            {
                content = encoding == "base64" ? Convert.FromBase64String(encoded) : Encoding.UTF8.GetBytes(encoded);
            }
            catch (FormatException)
            {
                return ValidationFailed("content is not valid base64");
            }

            var normalized = FakeGitStore.NormalizePath(path);
            var branch = Str(body, "branch") ?? repository.DefaultBranch;
            var head = repository.Head(branch);
            var branchSha = Str(body, "sha");
            var existing = head is null ? null : FakeGitStore.ReadFile(repository, head, normalized);
            if (existing is not null)
            {
                if (branchSha is null)
                {
                    return ValidationFailed("sha wasn't supplied");
                }

                if (branchSha != GitBlobHasher.Compute(existing).ToString())
                {
                    return Results.Json(new { message = "sha does not match" }, statusCode: StatusCodes.Status409Conflict);
                }
            }

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (head is not null)
            {
                foreach (var (filePath, fileContent) in FakeGitStore.Flatten(repository, FakeGitStore.FindCommit(repository, head)!.Tree))
                {
                    files[filePath] = fileContent;
                }
            }

            files[normalized] = content;
            var parents = head is null ? new List<CommitSha>() : [CommitSha.Parse(head).Value];
            var author = Signature(body?["author"], server.Options.DefaultSignature);
            var committer = Signature(body?["committer"], author);
            var message = Str(body, "message") ?? "Update " + normalized;
            var commit = FakeGitStore.CreateCommit(repository, files, parents, message, author, committer);
            FakeGitStore.SetRef(repository, branch, commit.ToString());
            var sha = GitBlobHasher.Compute(content).ToString();
            return Results.Json(new
            {
                content = new
                {
                    name = normalized[(normalized.LastIndexOf('/') + 1)..],
                    path = normalized,
                    sha,
                    size = content.Length,
                    type = "file",
                    encoding = "base64",
                    content = Convert.ToBase64String(content),
                    url = Url(context, $"/repos/{owner}/{repo}/contents/{normalized}"),
                },
                commit = new
                {
                    sha = commit.ToString(),
                    message,
                    url = Url(context, $"/repos/{owner}/{repo}/git/commits/{commit}"),
                    html_url = Url(context, $"/{owner}/{repo}/commit/{commit}"),
                },
            }, statusCode: existing is null ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        app.MapGet("/repos/{owner}/{repo}/commits", (HttpContext context, string owner, string repo) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            if (repository.IsEmpty)
            {
                return EmptyRepository();
            }

            var reference = context.Request.Query["sha"].ToString();
            var head = FakeGitStore.ResolveCommit(repository, reference.Length == 0 ? null : reference);
            if (head is null)
            {
                return NotFound();
            }

            var path = context.Request.Query["path"].ToString();
            var history = FakeGitStore.History(repository, head, path.Length == 0 ? null : path);
            var (items, next) = Page(context, history, server.Options);
            SetLink(context, next);
            return Results.Json(items.Select(commit => new
            {
                sha = commit.Sha,
                url = Url(context, $"/repos/{owner}/{repo}/commits/{commit.Sha}"),
                html_url = Url(context, $"/{owner}/{repo}/commit/{commit.Sha}"),
                commit = new
                {
                    message = commit.Message,
                    author = SignatureDto(commit.Author),
                    committer = SignatureDto(commit.Committer),
                },
            }));
        });
    }

    private static void MapDownloads(WebApplication app, FakeGitHubServer server)
    {
        app.MapGet("/repos/{owner}/{repo}/zipball/{**reference}", (HttpContext context, string owner, string repo, string? reference) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            var commit = FakeGitStore.ResolveCommit(repository, reference);
            if (commit is null)
            {
                return NotFound();
            }

            return Results.Redirect(Url(context, $"/codeload/{owner}/{repo}/zip/{commit}"));
        });

        app.MapGet("/codeload/{owner}/{repo}/zip/{**reference}", (HttpContext context, string owner, string repo, string? reference) =>
        {
            var repository = server.FindRepo(owner, repo);
            if (repository is null)
            {
                return NotFound();
            }

            var commit = FakeGitStore.ResolveCommit(repository, reference);
            var commitObject = commit is null ? null : FakeGitStore.FindCommit(repository, commit);
            if (commitObject is null)
            {
                return NotFound();
            }

            var files = FakeGitStore.Flatten(repository, commitObject.Tree);
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var prefix = $"{owner}-{repo}-{commit}/";
                foreach (var (path, content) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(prefix + path, CompressionLevel.Fastest);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var stream = entry.Open();
                    stream.Write(content);
                }
            }

            return Results.Bytes(buffer.ToArray(), "application/zip", $"{owner}-{repo}-{commit}.zip");
        });
    }

    private static IResult CreateRepository(
        HttpContext context,
        FakeGitHubServer server,
        JsonNode? body,
        string owner)
    {
        var name = Str(body, "name");
        if (name is null || name.Length == 0)
        {
            return ValidationFailed("name is required");
        }

        if (server.TryGetRepo(owner, name, out _))
        {
            return ValidationFailed("Repository already exists");
        }

        var isPrivate = Bool(body, "private") ?? true;
        var repository = server.CreateRepo(owner, name, isPrivate, true);
        if (Bool(body, "auto_init") ?? false)
        {
            server.CommitFiles(owner, name, repository.DefaultBranch,
                new Dictionary<string, string> { ["README.md"] = "# " + name + "\n" }, "Initial commit");
        }

        return Results.Json(RepoDto(context, repository), statusCode: StatusCodes.Status201Created);
    }

    private static object RepoDto(HttpContext context, FakeRepository repository) => new
    {
        id = repository.Id,
        name = repository.Name,
        full_name = repository.Owner + "/" + repository.Name,
        @private = repository.Private,
        owner = new
        {
            login = repository.Owner,
            id = repository.Id,
            type = "User",
        },
        html_url = Url(context, $"/{repository.Owner}/{repository.Name}"),
        default_branch = repository.DefaultBranch,
        archived = repository.Archived,
        disabled = false,
        visibility = repository.Private ? "private" : "public",
        permissions = new
        {
            admin = false,
            maintain = repository.AllowPush && !repository.Archived,
            push = repository.AllowPush && !repository.Archived,
            triage = repository.AllowPush && !repository.Archived,
            pull = true,
        },
        empty = repository.IsEmpty,
    };

    private static object RefDto(HttpContext context, string owner, string repo, string branch, string sha) => new
    {
        @ref = "refs/heads/" + branch,
        node_id = "REF_" + sha,
        url = Url(context, $"/repos/{owner}/{repo}/git/refs/heads/{branch}"),
        @object = new
        {
            sha,
            type = "commit",
            url = Url(context, $"/repos/{owner}/{repo}/git/commits/{sha}"),
        },
    };

    private static object CommitDto(HttpContext context, string owner, string repo, FakeGitCommit commit) => new
    {
        sha = commit.Sha,
        node_id = "COMMIT_" + commit.Sha,
        url = Url(context, $"/repos/{owner}/{repo}/git/commits/{commit.Sha}"),
        html_url = Url(context, $"/{owner}/{repo}/commit/{commit.Sha}"),
        author = SignatureDto(commit.Author),
        committer = SignatureDto(commit.Committer),
        message = commit.Message,
        tree = new
        {
            sha = commit.Tree,
            url = Url(context, $"/repos/{owner}/{repo}/git/trees/{commit.Tree}"),
        },
        parents = commit.Parents.Select(parent => new
        {
            sha = parent,
            url = Url(context, $"/repos/{owner}/{repo}/git/commits/{parent}"),
        }),
    };

    private static object SignatureDto(GitSignature signature) => new
    {
        name = signature.Name,
        email = signature.Email,
        date = signature.When.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
    };

    private static GitSignature Signature(JsonNode? node, GitSignature fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        var name = Str(node, "name") ?? fallback.Name;
        var email = Str(node, "email") ?? fallback.Email;
        var date = Str(node, "date");
        var when = date is not null
            && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : fallback.When;
        return new GitSignature(name, email, when);
    }

    private static async Task<JsonNode?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength == 0)
        {
            return null;
        }

        try
        {
            return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node, string name)
    {
        var value = node?[name];
        return value is null || value.GetValueKind() == JsonValueKind.Null ? null : value.GetValue<string>();
    }

    private static bool? Bool(JsonNode? node, string name)
    {
        var value = node?[name];
        return value is null || value.GetValueKind() == JsonValueKind.Null ? null : value.GetValue<bool>();
    }

    private static string Branch(string? refPath)
    {
        var path = (refPath ?? string.Empty).Trim('/');
        if (path.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return path["refs/heads/".Length..];
        }

        return path.StartsWith("heads/", StringComparison.Ordinal) ? path["heads/".Length..] : path;
    }

    private static void RemovePrefix(Dictionary<string, byte[]> files, string path)
    {
        foreach (var key in files.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            files.Remove(key);
        }
    }

    private static (IReadOnlyList<T> Items, string? Next) Page<T>(
        HttpContext context,
        IReadOnlyList<T> all,
        FakeGitHubOptions options)
    {
        var perPage = ParseInt(context.Request.Query["per_page"], options.PageSize);
        perPage = Math.Clamp(perPage, 1, options.PageSizeMax);
        var page = Math.Max(1, ParseInt(context.Request.Query["page"], 1));
        var skip = (page - 1) * perPage;
        var items = all.Skip(skip).Take(perPage).ToList();
        var next = skip + perPage < all.Count
            ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}?per_page={perPage}&page={page + 1}"
            : null;
        return (items, next);
    }

    private static int ParseInt(StringValues value, int fallback) =>
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static void SetLink(HttpContext context, string? next)
    {
        if (next is not null)
        {
            context.Response.Headers["Link"] = $"<{next}>; rel=\"next\"";
        }
    }

    private static string Url(HttpContext context, string path) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{path}";

    private static void WriteRateLimitHeaders(HttpContext context, int remaining, FakeGitHubOptions options)
    {
        context.Response.Headers["x-ratelimit-limit"] = options.RateLimit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["x-ratelimit-remaining"] = Math.Clamp(remaining, 0, options.RateLimit).ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["x-ratelimit-used"] = Math.Clamp(options.RateLimit - remaining, 0, options.RateLimit).ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["x-ratelimit-reset"] = options.RateLimitReset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["x-ratelimit-resource"] = "core";
    }

    private static bool IsPublicPath(PathString path) =>
        path.StartsWithSegments("/login") || path.StartsWithSegments("/codeload");

    private static IResult NotFound() => Results.Json(new { message = "Not Found" }, statusCode: StatusCodes.Status404NotFound);

    private static IResult Forbidden() => Results.Json(new { message = "Forbidden" }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult EmptyRepository() => Results.Json(
        new { message = "Git Repository is empty." },
        statusCode: StatusCodes.Status409Conflict);

    private static IResult ValidationFailed(string message) => Results.Json(
        new { message, documentation_url = "https://docs.github.com/rest" },
        statusCode: StatusCodes.Status422UnprocessableEntity);

    private static async Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = JsonContentType;
        await context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted).ConfigureAwait(false);
    }
}
