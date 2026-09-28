using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PlaybackCard.Notifications;

/// <summary>
/// Optionally enriches a playback notification with IMDb rating and vote-count data from OMDb.
/// Jellyfin metadata remains the authoritative fallback and notification delivery never depends
/// on this external service succeeding.
/// </summary>
public interface IMediaRatingService
{
    Task EnrichAsync(PlaybackNotificationPayload payload, string? omdbApiKey, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class MediaRatingService : IMediaRatingService, IDisposable
{
    private static readonly Regex ImdbIdRegex = new("^tt[0-9]{7,10}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ApiKeyRegex = new("^[a-zA-Z0-9]{6,64}$", RegexOptions.Compiled);
    private static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly ILogger<MediaRatingService> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly SemaphoreSlim _lookupLock = new(1, 1);

    public MediaRatingService(ILogger<MediaRatingService> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                CheckCertificateRevocationList = true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            _ownsClient = true;
        }
    }

    public void Dispose()
    {
        _lookupLock.Dispose();
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    public static bool ValidateApiKey(string? apiKey)
    {
        return !string.IsNullOrWhiteSpace(apiKey) && ApiKeyRegex.IsMatch(apiKey.Trim());
    }

    /// <inheritdoc />
    public async Task EnrichAsync(PlaybackNotificationPayload payload, string? omdbApiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var imdbId = payload.ImdbId?.Trim();
        var apiKey = omdbApiKey?.Trim();
        if (!ImdbIdRegex.IsMatch(imdbId ?? string.Empty) || !ValidateApiKey(apiKey))
        {
            return;
        }

        var cacheKey = BuildCacheKey(imdbId!, apiKey!);
        if (TryApplyCached(cacheKey, payload))
        {
            return;
        }

        await _lookupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryApplyCached(cacheKey, payload))
            {
                return;
            }

            var result = await LookupAsync(imdbId!, apiKey!, cancellationToken).ConfigureAwait(false);
            _cache[cacheKey] = new CacheEntry(result, DateTimeOffset.UtcNow.Add(result != null ? SuccessCacheTtl : FailureCacheTtl));
            Apply(result, payload);
        }
        finally
        {
            _lookupLock.Release();
        }
    }

    private bool TryApplyCached(string cacheKey, PlaybackNotificationPayload payload)
    {
        if (!_cache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _cache.TryRemove(cacheKey, out _);
            return false;
        }

        Apply(cached.Value, payload);
        return true;
    }

    private async Task<RatingData?> LookupAsync(string imdbId, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(
                "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(apiKey) +
                "&i=" + Uri.EscapeDataString(imdbId) + "&r=json");
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[MediaRatingService] OMDb lookup returned HTTP {StatusCode}; using Jellyfin metadata fallback.", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("Response", out var responseProperty) &&
                responseProperty.ValueKind == JsonValueKind.String &&
                string.Equals(responseProperty.GetString(), "False", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var rating = ReadString(root, "imdbRating");
            var votesText = ReadString(root, "imdbVotes");
            if (string.Equals(rating, "N/A", StringComparison.OrdinalIgnoreCase)) rating = null;
            if (string.Equals(votesText, "N/A", StringComparison.OrdinalIgnoreCase)) votesText = null;

            int? votes = null;
            if (!string.IsNullOrWhiteSpace(votesText) &&
                int.TryParse(votesText, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedVotes) &&
                parsedVotes >= 0)
            {
                votes = parsedVotes;
            }

            return string.IsNullOrWhiteSpace(rating) && !votes.HasValue ? null : new RatingData(rating, votes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("[MediaRatingService] OMDb lookup failed; using Jellyfin metadata fallback: {Error}", SecretRedactor.SanitizeExceptionMessage(ex));
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static void Apply(RatingData? data, PlaybackNotificationPayload payload)
    {
        if (data == null)
        {
            return;
        }

        payload.ImdbRating = data.Rating;
        payload.ImdbVoteCount = data.Votes;
    }

    private static string BuildCacheKey(string imdbId, string apiKey)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).Substring(0, 12);
        return imdbId.ToLowerInvariant() + ":" + fingerprint;
    }

    private sealed record RatingData(string? Rating, int? Votes);

    private sealed record CacheEntry(RatingData? Value, DateTimeOffset ExpiresAt);
}

