using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xproapi;

/// <summary>Your per-minute limit and the requests left this minute. Both are null before the first response.</summary>
public readonly record struct RateLimit(int? Limit, int? Remaining);

/// <summary>
/// Client for the Xproapi public API (https://xproapi.com/docs).
/// <code>
///   var x = new XproapiClient(Environment.GetEnvironmentVariable("XPROAPI_KEY")!);
///   var user = (await x.UserInfoAsync("elonmusk"))["data"]!;
///   await foreach (var follower in x.PaginateAsync("followers", new() { ["userName"] = "elonmusk" }, maxPages: 3)) { ... }
/// </code>
/// Every method returns the decoded JSON response. Errors throw an <see cref="XproapiException"/> subclass.
/// Rate-limited (429) and temporarily unavailable (502/503) requests are retried automatically.
/// </summary>
public sealed class XproapiClient
{
    public const string DefaultBaseUrl = "https://xproapi.com/api";
    public const string Version = "1.0.0";

    /// <summary>Endpoint name => (path, key of the list in the response), used by PaginateAsync.</summary>
    private static readonly Dictionary<string, (string Path, string Key)> Paginated = new()
    {
        ["searchUsers"] = ("/twitter/user/search", "users"),
        ["followers"] = ("/twitter/user/followers", "followers"),
        ["followings"] = ("/twitter/user/followings", "followings"),
        ["lastTweets"] = ("/twitter/user/last_tweets", "tweets"),
        ["tweetTimeline"] = ("/twitter/user/tweet_timeline", "tweets"),
        ["mentions"] = ("/twitter/user/mentions", "tweets"),
        ["advancedSearch"] = ("/twitter/tweet/advanced_search", "tweets"),
        ["replies"] = ("/twitter/tweet/replies", "replies"),
        ["repliesV2"] = ("/twitter/tweet/replies/v2", "tweets"),
        ["quotes"] = ("/twitter/tweet/quotes", "tweets"),
        ["retweeters"] = ("/twitter/tweet/retweeters", "users"),
        ["threadContext"] = ("/twitter/tweet/thread_context", "replies"),
    };

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly HttpClient _http;

    /// <summary>Waits between retries (replaced in tests).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = Task.Delay;

    /// <param name="apiKey">From your panel → API → API keys.</param>
    /// <param name="baseUrl">Defaults to https://xproapi.com/api.</param>
    /// <param name="timeout">Time allowed per request (default 30 seconds).</param>
    /// <param name="maxRetries">Retries for 429 / 502 / 503 / network errors (default 2, 0 = never).</param>
    /// <param name="httpClient">Your own HttpClient (for proxies, IHttpClientFactory or tests).</param>
    public XproapiClient(string apiKey, string? baseUrl = null, TimeSpan? timeout = null, int maxRetries = 2, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("An API key is required. Create one in your panel under API → API keys.", nameof(apiKey));
        }

        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _maxRetries = maxRetries;
        // The per-request timeout below applies; the client's own would cut long retries short.
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Rate limit of the last response.</summary>
    public RateLimit RateLimit { get; private set; }

    // ---- Users ---------------------------------------------------------------------------------

    /// <summary>Profile of one user. Response: { data: User }.</summary>
    public Task<JsonObject> UserInfoAsync(string userName, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/info", new() { ["userName"] = userName }, cancellationToken);

    /// <summary>Profiles of up to 100 users in one call (cheaper per profile). Response: { users: User[] }.</summary>
    public Task<JsonObject> BatchUserInfoByUsernamesAsync(IEnumerable<string> userNames, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/batch_info_by_usernames", new() { ["userNames"] = string.Join(',', userNames) }, cancellationToken);

    /// <summary>"About this account": country, join date, username changes... Response: { data: {...} }.</summary>
    public Task<JsonObject> UserAboutAsync(string userName, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user_about", new() { ["userName"] = userName }, cancellationToken);

    /// <summary>People search. Response: { users: User[], has_next_page, next_cursor }.</summary>
    public Task<JsonObject> SearchUsersAsync(string query, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/search", new() { ["query"] = query, ["cursor"] = cursor }, cancellationToken);

    /// <summary>Followers, newest first. pageSize 20–200 (default 200). Response: { followers: User[], has_next_page, next_cursor }.</summary>
    public Task<JsonObject> FollowersAsync(string userName, string? cursor = null, int? pageSize = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/followers", new() { ["userName"] = userName, ["cursor"] = cursor, ["pageSize"] = pageSize }, cancellationToken);

    /// <summary>Accounts the user follows, newest first. Response: { followings: User[], has_next_page, next_cursor }.</summary>
    public Task<JsonObject> FollowingsAsync(string userName, string? cursor = null, int? pageSize = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/followings", new() { ["userName"] = userName, ["cursor"] = cursor, ["pageSize"] = pageSize }, cancellationToken);

    /// <summary>The user's tweets and retweets, newest first. Pass userName or userId.</summary>
    public Task<JsonObject> LastTweetsAsync(string? userName = null, string? userId = null, bool? includeReplies = null, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/last_tweets", new() { ["userName"] = userName, ["userId"] = userId, ["includeReplies"] = includeReplies, ["cursor"] = cursor }, cancellationToken);

    /// <summary>Like LastTweetsAsync; with includeReplies + includeParentTweet the tweets being replied to are kept too.</summary>
    public Task<JsonObject> TweetTimelineAsync(string? userName = null, string? userId = null, bool? includeReplies = null, bool? includeParentTweet = null, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/tweet_timeline", new() { ["userName"] = userName, ["userId"] = userId, ["includeReplies"] = includeReplies, ["includeParentTweet"] = includeParentTweet, ["cursor"] = cursor }, cancellationToken);

    /// <summary>Tweets by others that mention the user. sinceTime / untilTime are unix seconds.</summary>
    public Task<JsonObject> MentionsAsync(string userName, long? sinceTime = null, long? untilTime = null, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/user/mentions", new() { ["userName"] = userName, ["sinceTime"] = sinceTime, ["untilTime"] = untilTime, ["cursor"] = cursor }, cancellationToken);

    // ---- Tweets --------------------------------------------------------------------------------

    /// <summary>Up to 100 tweets by id. Response: { tweets: Tweet[] }.</summary>
    public Task<JsonObject> TweetsByIdsAsync(IEnumerable<string> tweetIds, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/tweets", new() { ["tweet_ids"] = string.Join(',', tweetIds) }, cancellationToken);

    /// <summary>X search with its operators (from:, since:, min_faves:, lang:, filter:...). queryType: Latest or Top.</summary>
    public Task<JsonObject> AdvancedSearchAsync(string query, string queryType = "Latest", string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/tweet/advanced_search", new() { ["query"] = query, ["queryType"] = queryType, ["cursor"] = cursor }, cancellationToken);

    /// <summary>Replies in a tweet's conversation, newest first. Response: { replies: Tweet[], ... }.</summary>
    public Task<JsonObject> RepliesAsync(string tweetId, long? sinceTime = null, long? untilTime = null, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/tweet/replies", new() { ["tweetId"] = tweetId, ["sinceTime"] = sinceTime, ["untilTime"] = untilTime, ["cursor"] = cursor }, cancellationToken);

    /// <summary>Replies ranked like X shows them. queryType: Relevance, Latest or Likes. Response: { tweets: Tweet[], ... }.</summary>
    public Task<JsonObject> RepliesV2Async(string tweetId, string queryType = "Relevance", string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/tweet/replies/v2", new() { ["tweetId"] = tweetId, ["queryType"] = queryType, ["cursor"] = cursor }, cancellationToken);

    /// <summary>Quote tweets of a tweet.</summary>
    public Task<JsonObject> QuotesAsync(string tweetId, long? sinceTime = null, long? untilTime = null, bool? includeReplies = null, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/tweet/quotes", new() { ["tweetId"] = tweetId, ["sinceTime"] = sinceTime, ["untilTime"] = untilTime, ["includeReplies"] = includeReplies, ["cursor"] = cursor }, cancellationToken);

    /// <summary>Users who retweeted a tweet. Response: { users: User[], ... }.</summary>
    public Task<JsonObject> RetweetersAsync(string tweetId, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/tweet/retweeters", new() { ["tweetId"] = tweetId, ["cursor"] = cursor }, cancellationToken);

    /// <summary>The whole conversation around a tweet, in reading order. Response: { replies: Tweet[], ... }.</summary>
    public Task<JsonObject> ThreadContextAsync(string tweetId, string? cursor = null, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/tweet/thread_context", new() { ["tweetId"] = tweetId, ["cursor"] = cursor }, cancellationToken);

    /// <summary>The long-form article attached to a tweet. Response: { article: {...} }.</summary>
    public Task<JsonObject> ArticleAsync(string tweetId, CancellationToken cancellationToken = default)
        => GetAsync("/twitter/article", new() { ["tweet_id"] = tweetId }, cancellationToken);

    // ---- Pagination, raw requests --------------------------------------------------------------

    /// <summary>
    /// Every item of a paginated endpoint, page after page, following next_cursor.
    /// Each page is a separate (charged) request; stop early with maxPages or by leaving the loop.
    /// </summary>
    /// <param name="endpoint">searchUsers, followers, followings, lastTweets, tweetTimeline, mentions, advancedSearch, replies, repliesV2, quotes, retweeters or threadContext.</param>
    /// <param name="parameters">The endpoint's query parameters under the API's own names (without cursor).</param>
    /// <param name="maxPages">The most pages to request; null for no cap.</param>
    public IAsyncEnumerable<JsonObject> PaginateAsync(string endpoint, Dictionary<string, object?>? parameters = null, int? maxPages = null, CancellationToken cancellationToken = default)
    {
        if (!Paginated.TryGetValue(endpoint, out var entry))
        {
            throw new ArgumentException($"{endpoint} isn't paginated. Use one of: {string.Join(", ", Paginated.Keys)}.", nameof(endpoint));
        }

        return Pages(entry.Path, entry.Key, parameters, maxPages, cancellationToken);
    }

    private async IAsyncEnumerable<JsonObject> Pages(string path, string key, Dictionary<string, object?>? parameters, int? maxPages, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = parameters is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(parameters);
        var pages = 0;
        string cursor;

        do
        {
            var page = await GetAsync(path, query, cancellationToken).ConfigureAwait(false);
            pages++;

            if (page[key] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is JsonObject value)
                    {
                        yield return value;
                    }
                }
            }

            cursor = page["has_next_page"] is JsonValue more && more.TryGetValue<bool>(out var hasNext) && hasNext ? page["next_cursor"]?.ToString() ?? "" : "";
            query["cursor"] = cursor;
        }
        while (cursor != "" && (maxPages is null || pages < maxPages));
    }

    /// <summary>
    /// Any GET endpoint under the base URL, e.g. GetAsync("/twitter/user/info", new() { ["userName"] = "jack" }).
    /// Null and empty parameters are left out; booleans are sent as "true" / "false".
    /// </summary>
    public async Task<JsonObject> GetAsync(string path, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();

        foreach (var (name, value) in parameters ?? new Dictionary<string, object?>())
        {
            var text = value switch
            {
                null => "",
                bool flag => flag ? "true" : "false",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };

            if (text != "")
            {
                query.Append(query.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(text));
            }
        }

        var url = $"{_baseUrl}/{path.TrimStart('/')}{query}";

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            string content;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", $"xproapi-csharp/{Version}");

                response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception cause) when (cause is HttpRequestException or IOException || (cause is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A network failure or our own timeout (not the caller cancelling).
                if (attempt < _maxRetries)
                {
                    await Sleep(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new XproapiException($"Network error: {cause.Message}", innerException: cause);
            }

            using (response)
            {
                RateLimit = new RateLimit(IntHeader(response, "X-RateLimit-Limit"), IntHeader(response, "X-RateLimit-Remaining"));

                var body = Decode(content);

                if (response.IsSuccessStatusCode && body is not null)
                {
                    return body;
                }

                var status = (int)response.StatusCode;
                var retryAfter = IntHeader(response, "Retry-After");

                if (status is 429 or 502 or 503 && attempt < _maxRetries)
                {
                    await Sleep(Backoff(attempt, retryAfter), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw XproapiException.FromResponse(status, body, retryAfter);
            }
        }
    }

    private static JsonObject? Decode(string content)
    {
        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The wait before a retry: Retry-After when sent (capped at 60s), else 1s, 2s, 4s…</summary>
    private static TimeSpan Backoff(int attempt, int? retryAfter)
        => TimeSpan.FromSeconds(retryAfter is int seconds ? Math.Clamp(seconds, 1, 60) : Math.Pow(2, attempt));

    private static int? IntHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
