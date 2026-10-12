// Offline tests for the C# client: a fake HTTP handler stands in for the API. Run: dotnet run --project tests
using System.Net;
using System.Text;
using System.Text.Json;
using Xproapi;

var passed = 0;
var failed = 0;

async Task Test(string name, Func<Task> body)
{
    try
    {
        await body();
        passed++;
        Console.WriteLine($"ok   {name}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL {name}\n       {e.GetType().Name}: {e.Message}");
    }
}

await Test("sends the API key and builds the query, leaving out empty parameters", async () =>
{
    var fake = new Fake([Reply(200, new { followers = Array.Empty<object>(), has_next_page = false, next_cursor = "" })]);

    await fake.Client.FollowersAsync("jack", cursor: null, pageSize: 50);

    Equal("https://api.test/api/twitter/user/followers?userName=jack&pageSize=50", fake.Requests[0].Url);
    Equal("xp_test_key", fake.Requests[0].ApiKey);
});

await Test("sends booleans as true / false and joins id lists", async () =>
{
    var fake = new Fake([Reply(200, new { tweets = Array.Empty<object>() }), Reply(200, new { tweets = Array.Empty<object>() })]);

    await fake.Client.QuotesAsync("123", includeReplies: false);
    await fake.Client.TweetsByIdsAsync(["1", "2", "3"]);

    Check(fake.Requests[0].Url.Contains("includeReplies=false"), fake.Requests[0].Url);
    Check(fake.Requests[1].Url.EndsWith("tweet_ids=1%2C2%2C3"), fake.Requests[1].Url);
});

await Test("applies default query types that arguments can replace", async () =>
{
    var fake = new Fake([Reply(200, new { tweets = Array.Empty<object>() }), Reply(200, new { tweets = Array.Empty<object>() })]);

    await fake.Client.AdvancedSearchAsync("from:jack");
    await fake.Client.RepliesV2Async("123", queryType: "Likes");

    Check(fake.Requests[0].Url.EndsWith("?query=from%3Ajack&queryType=Latest"), fake.Requests[0].Url);
    Check(fake.Requests[1].Url.EndsWith("?tweetId=123&queryType=Likes"), fake.Requests[1].Url);
});

await Test("returns the decoded body and remembers the rate limit headers", async () =>
{
    var fake = new Fake([Reply(200, new { data = new { userName = "jack", followers = 6500000 } }, ("X-RateLimit-Limit", "60"), ("X-RateLimit-Remaining", "59"))]);

    Equal(new RateLimit(null, null), fake.Client.RateLimit);

    var user = (await fake.Client.UserInfoAsync("jack"))["data"]!;

    Equal("jack", user["userName"]!.GetValue<string>());
    Equal(6500000, user["followers"]!.GetValue<int>());
    Equal(new RateLimit(60, 59), fake.Client.RateLimit);
});

await Test("maps error statuses to exception classes with the API message", async () =>
{
    var expected = new Dictionary<int, Type>
    {
        [400] = typeof(BadRequestException), [401] = typeof(AuthenticationException), [402] = typeof(InsufficientCreditsException),
        [403] = typeof(PermissionDeniedException), [404] = typeof(NotFoundException), [500] = typeof(ServiceUnavailableException),
    };

    foreach (var (status, type) in expected)
    {
        var e = await Thrown(() => new Fake([Error(status, $"Nope {status}")], maxRetries: 0).Client.UserInfoAsync("jack"));

        Equal(type, e.GetType());
        Equal(status, e.Status);
        Equal($"Nope {status}", e.Message);
        Equal("error", e.Body!["status"]!.GetValue<string>());
    }
});

await Test("retries a 429 after Retry-After, then succeeds", async () =>
{
    var fake = new Fake([Error(429, "Slow down", ("Retry-After", "3")), Reply(200, new { data = new { userName = "jack" } })]);

    await fake.Client.UserInfoAsync("jack");

    Equal(2, fake.Requests.Count);
    Equal("3", string.Join(",", fake.Slept.Select(wait => wait.TotalSeconds)));
});

await Test("gives up after maxRetries and throws RateLimitException carrying RetryAfter", async () =>
{
    var fake = new Fake([Error(429, "Slow down", ("Retry-After", "5")), Error(429, "Slow down", ("Retry-After", "5"))], maxRetries: 1);

    var e = await Thrown(() => fake.Client.UserInfoAsync("jack"));

    Equal(typeof(RateLimitException), e.GetType());
    Equal(5, e.RetryAfter);
    Equal(2, fake.Requests.Count);
});

await Test("does not retry client errors", async () =>
{
    var fake = new Fake([Error(400, "userName is required.")]);

    Equal(typeof(BadRequestException), (await Thrown(() => fake.Client.GetAsync("/twitter/user/info"))).GetType());
    Equal(1, fake.Requests.Count);
});

await Test("retries network errors, then reports them with status 0", async () =>
{
    var fake = new Fake([], maxRetries: 1, fail: new HttpRequestException("connection refused"));

    var e = await Thrown(() => fake.Client.UserInfoAsync("jack"));

    Equal(typeof(XproapiException), e.GetType());
    Equal(0, e.Status);
    Equal(2, fake.Requests.Count);
    Equal("1", string.Join(",", fake.Slept.Select(wait => wait.TotalSeconds)));
});

await Test("a cancelled token stops without retrying", async () =>
{
    var fake = new Fake([Reply(200, new { })]);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();

    try
    {
        await fake.Client.UserInfoAsync("jack", cancelled.Token);
        throw new InvalidOperationException("no exception was thrown");
    }
    catch (OperationCanceledException)
    {
        Equal(0, fake.Slept.Count);
    }
});

await Test("PaginateAsync follows next_cursor across pages and stops at maxPages", async () =>
{
    var fake = new Fake([Page(true, "c1", "1", "2"), Page(true, "c2", "3"), Page(false, "", "4")]);
    var all = new List<string>();

    await foreach (var user in fake.Client.PaginateAsync("followers", new() { ["userName"] = "jack" }))
    {
        all.Add(user["id"]!.GetValue<string>());
    }

    Equal("1,2,3,4", string.Join(",", all));
    Check(!fake.Requests[0].Url.Contains("cursor="), fake.Requests[0].Url);
    Check(fake.Requests[1].Url.Contains("cursor=c1"), fake.Requests[1].Url);
    Check(fake.Requests[2].Url.Contains("cursor=c2"), fake.Requests[2].Url);

    var capped = new Fake([Page(true, "c1", "1"), Page(true, "c2", "2")]);
    var count = 0;

    await foreach (var _ in capped.Client.PaginateAsync("followers", new() { ["userName"] = "jack" }, maxPages: 1))
    {
        count++;
    }

    Equal(1, count);
    Equal(1, capped.Requests.Count);
});

await Test("rejects an empty key and unknown paginated endpoints", () =>
{
    Check(Raises<ArgumentException>(() => new XproapiClient(" ")), "an empty key was accepted");

    var fake = new Fake([]);

    Check(Raises<ArgumentException>(() => fake.Client.PaginateAsync("userInfo")), "userInfo was accepted");
    Equal(0, fake.Requests.Count);

    return Task.CompletedTask;
});

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");

return failed > 0 ? 1 : 0;

static (int Status, string Body, (string, string)[] Headers) Reply(int status, object body, params (string, string)[] headers)
    => (status, JsonSerializer.Serialize(body), headers);

static (int Status, string Body, (string, string)[] Headers) Error(int status, string message, params (string, string)[] headers)
    => Reply(status, new { status = "error", error = status, message }, headers);

static (int Status, string Body, (string, string)[] Headers) Page(bool more, string cursor, params string[] ids)
    => Reply(200, new { followers = ids.Select(id => new { id }), has_next_page = more, next_cursor = cursor });

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected <{expected}> but got <{actual}>");
    }
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task<XproapiException> Thrown(Func<Task> body)
{
    try
    {
        await body();
    }
    catch (XproapiException e)
    {
        return e;
    }

    throw new InvalidOperationException("no XproapiException was thrown");
}

static bool Raises<T>(Action body) where T : Exception
{
    try
    {
        body();
    }
    catch (T)
    {
        return true;
    }

    return false;
}

/// <summary>A client whose HTTP handler answers from the given replies in order and records each request.</summary>
sealed class Fake : HttpMessageHandler
{
    private readonly Queue<(int Status, string Body, (string, string)[] Headers)> _replies;
    private readonly Exception? _fail;

    public Fake((int Status, string Body, (string, string)[] Headers)[] replies, int maxRetries = 2, Exception? fail = null)
    {
        _replies = new(replies);
        _fail = fail;
        Client = new XproapiClient("xp_test_key", baseUrl: "https://api.test/api/", maxRetries: maxRetries, httpClient: new HttpClient(this))
        {
            Sleep = (wait, _) =>
            {
                Slept.Add(wait);
                return Task.CompletedTask;
            },
        };
    }

    public XproapiClient Client { get; }

    public List<(string Url, string? ApiKey)> Requests { get; } = [];

    public List<TimeSpan> Slept { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("X-API-Key", out var key) ? key.First() : null));

        if (_fail is not null)
        {
            throw _fail;
        }

        var (status, body, headers) = _replies.Dequeue();
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return Task.FromResult(response);
    }
}
