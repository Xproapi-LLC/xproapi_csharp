# xproapi (C#)

C# client for the [Xproapi](https://xproapi.com/docs) X (Twitter) data API. .NET 8+, no dependencies (uses `System.Text.Json`).

## Install

Clone the repository next to your project and reference it:

```bash
git clone https://github.com/Xproapi-LLC/xproapi_csharp.git
dotnet add reference xproapi_csharp/src/Xproapi.csproj
```

## Usage

```csharp
using Xproapi;

var x = new XproapiClient(Environment.GetEnvironmentVariable("XPROAPI_KEY")!);

var user = (await x.UserInfoAsync("jack"))["data"]!;
Console.WriteLine($"{user["name"]} has {user["followers"]!.GetValue<long>():N0} followers");

// One page (up to 200), then the next
var page = await x.FollowersAsync("jack", pageSize: 200);
var next = await x.FollowersAsync("jack", cursor: page["next_cursor"]!.ToString());

// Every page, capped at 5 requests (parameters use the API's own names)
await foreach (var follower in x.PaginateAsync("followers", new() { ["userName"] = "jack" }, maxPages: 5))
{
    Console.WriteLine(follower["userName"]);
}

// Tweets
await x.LastTweetsAsync(userName: "jack", includeReplies: false);
await x.AdvancedSearchAsync("from:jack min_faves:100", queryType: "Top");
await x.TweetsByIdsAsync(["20", "1800000000000000000"]);
await x.RepliesAsync("https://x.com/jack/status/20");   // ids or links
```

Methods return the API's JSON as a `JsonObject` (`System.Text.Json.Nodes`): read values with `["key"]` and `GetValue<T>()`. Every method takes an optional `CancellationToken`.

### Options

```csharp
new XproapiClient(
    apiKey: "xp_...",
    baseUrl: "https://xproapi.com/api",     // default
    timeout: TimeSpan.FromSeconds(30),      // per request
    maxRetries: 2,                          // for 429 / 502 / 503 / network errors; 0 = off
    httpClient: null                        // your own HttpClient (IHttpClientFactory, proxies, tests)
);
```

### Errors

```csharp
try
{
    await x.UserInfoAsync("someone");
}
catch (NotFoundException)
{
    // no such user
}
catch (InsufficientCreditsException)
{
    // top up; nothing was charged
}
catch (RateLimitException e)
{
    await Task.Delay(TimeSpan.FromSeconds(e.RetryAfter ?? 1));
}
catch (XproapiException e)
{
    Console.Error.WriteLine($"Xproapi {e.Status}: {e.Message}");
}
```

`BadRequestException` (400), `AuthenticationException` (401), `InsufficientCreditsException` (402), `PermissionDeniedException` (403), `NotFoundException` (404), `RateLimitException` (429) and `ServiceUnavailableException` (5xx) all extend `XproapiException`. Network failures throw the base class with `Status` 0.

### Rate limit

```csharp
x.RateLimit; // RateLimit { Limit = 60, Remaining = 57 } from the last response
```

### Anything else

`await x.GetAsync("/twitter/user/info", new() { ["userName"] = "jack" })` calls any GET endpoint with the same auth, retries and errors.

## Tests

The offline tests are a console program (no test framework):

```bash
dotnet run --project tests
```
