using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PlaybackCard.Notifications;
using Xunit;

namespace JellyfinPlaybackCard.Tests;

public sealed class DiscordLiveUpdateTests
{
    [Fact]
    public async Task LiveSession_CreatesThenEditsTheSameMessage()
    {
        const string webhook = "https://discord.com/api/webhooks/123456789/validToken123";
        const string sessionKey = "session-live-1";
        var callCount = 0;
        var handler = new TestHttpMessageHandler
        {
            HandlerFunc = (_, _) =>
            {
                callCount++;
                return Task.FromResult(callCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"id\":\"987654321012345678\"}")
                    }
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        };

        using var client = new HttpClient(handler);
        using var sender = new DiscordWebhookSender(new TestLogger<DiscordWebhookSender>(), client);

        using (DiscordWebhookSender.UseSession(sessionKey))
        {
            var startResult = await sender.SendAsync(CreatePayload(NotificationEventType.Start, 0), webhook, CancellationToken.None);
            Assert.True(startResult.Success);
        }

        using (DiscordWebhookSender.UseSession(sessionKey))
        {
            var progressResult = await sender.SendAsync(CreatePayload(NotificationEventType.Progress, 11), webhook, CancellationToken.None);
            Assert.True(progressResult.Success);
        }

        Assert.Equal(2, handler.CapturedRequests.Count);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequests[0].Method);
        Assert.Equal("?wait=true", handler.CapturedRequests[0].RequestUri?.Query);
        Assert.Equal(HttpMethod.Patch, handler.CapturedRequests[1].Method);
        Assert.Equal(
            "/api/webhooks/123456789/validToken123/messages/987654321012345678",
            handler.CapturedRequests[1].RequestUri?.AbsolutePath);
        Assert.Contains("11%", handler.CapturedContents[1]);
    }

    [Fact]
    public async Task TerminalEvent_EditsThenClosesTheLiveMessage()
    {
        const string webhook = "https://discord.com/api/webhooks/123456789/validToken123";
        const string sessionKey = "session-live-stop";
        var callCount = 0;
        var handler = new TestHttpMessageHandler
        {
            HandlerFunc = (_, _) =>
            {
                callCount++;
                return Task.FromResult(callCount == 1 || callCount == 3
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"id\":\"98765432101234567{callCount}\"}}")
                    }
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        };

        using var client = new HttpClient(handler);
        using var sender = new DiscordWebhookSender(new TestLogger<DiscordWebhookSender>(), client);

        using (DiscordWebhookSender.UseSession(sessionKey))
        {
            await sender.SendAsync(CreatePayload(NotificationEventType.Start, 0), webhook, CancellationToken.None);
            await sender.SendAsync(CreatePayload(NotificationEventType.Stop, 25), webhook, CancellationToken.None);
            await sender.SendAsync(CreatePayload(NotificationEventType.Progress, 26), webhook, CancellationToken.None);
        }

        Assert.Equal(HttpMethod.Patch, handler.CapturedRequests[1].Method);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequests[2].Method);
        Assert.Equal("?wait=true", handler.CapturedRequests[2].RequestUri?.Query);
    }

    private static PlaybackNotificationPayload CreatePayload(NotificationEventType eventType, int percentage) => new()
    {
        EventType = eventType,
        Timestamp = DateTimeOffset.UtcNow,
        MediaTitle = "Interstellar",
        PlayMethod = "DirectPlay",
        VideoStatus = "Video Direct",
        AudioStatus = "Audio Direct",
        Position = TimeSpan.FromMinutes(percentage),
        TotalDuration = TimeSpan.FromMinutes(100),
        PlaybackPercentage = percentage
    };
}
