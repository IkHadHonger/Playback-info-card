using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PlaybackCard.Notifications;
using Xunit;

namespace JellyfinPlaybackCard.Tests;

public class MediaRatingServiceTests
{
    [Fact]
    public async Task EnrichAsync_AddsImdbRatingAndVotes()
    {
        var handler = new TestHttpMessageHandler
        {
            HandlerFunc = (request, cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Response\":\"True\",\"imdbRating\":\"8.7\",\"imdbVotes\":\"2,200,000\"}", Encoding.UTF8, "application/json")
            })
        };
        using var client = new HttpClient(handler);
        using var service = new MediaRatingService(new TestLogger<MediaRatingService>(), client);
        var payload = new PlaybackNotificationPayload { ImdbId = "tt0816692", CommunityRating = 8.6f };

        await service.EnrichAsync(payload, "abc12345", CancellationToken.None);

        Assert.Equal("8.7", payload.ImdbRating);
        Assert.Equal(2200000, payload.ImdbVoteCount);
        Assert.Contains("tt0816692", handler.CapturedRequests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task EnrichAsync_LeavesJellyfinFallbackWhenOmdbFails()
    {
        var handler = new TestHttpMessageHandler
        {
            HandlerFunc = (request, cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };
        using var client = new HttpClient(handler);
        using var service = new MediaRatingService(new TestLogger<MediaRatingService>(), client);
        var payload = new PlaybackNotificationPayload { ImdbId = "tt0816692", CommunityRating = 8.6f };

        await service.EnrichAsync(payload, "abc12345", CancellationToken.None);

        Assert.Null(payload.ImdbRating);
        Assert.Null(payload.ImdbVoteCount);
        Assert.Equal(8.6f, payload.CommunityRating);
    }
}

