using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.PlaybackCard.Notifications.Consumers;

/// <summary>
/// Truthful play-method classification of a session's transcoding state, shared by every consumer
/// that needs to know whether a stream is really Direct, a Remux, or a Transcode.
/// </summary>
public readonly struct TranscodeClassification
{
    public string PlayMethod { get; init; }
    public bool? IsVideoDirect { get; init; }
    public bool? IsAudioDirect { get; init; }
    public bool IsContainerRemux { get; init; }
    public string VideoStatus { get; init; }
    public string AudioStatus { get; init; }
}

/// <summary>
/// Safe mapper that projects Jellyfin SessionInfo and BaseItem into a minimal internal PlaybackEventRecord.
/// Strictly enforces privacy-by-omission: never touches or maps RemoteEndPoint, IP addresses,
/// file paths, auth tokens, cookies, or raw session JSON.
/// Accurately derives truthful video/audio direct status, remux distinction, and transcode reasons.
/// </summary>
public static class PlaybackEventMapper
{
    /// <summary>
    /// Derives play method, direct/transcode status per stream, and the truthful "Remux" distinction
    /// shared by every consumer that reads Jellyfin's <see cref="TranscodingInfo"/> -- the notification
    /// payload mapper here and <c>PlaybackSelfSessionsController.MapSessionToDto</c> both need the exact
    /// same classification of the same session data.
    /// </summary>
    public static TranscodeClassification ClassifyPlayback(TranscodingInfo? transcodingInfo, PlayMethod? rawPlayMethod)
    {
        bool? isVideoDirect = null;
        bool? isAudioDirect = null;
        var isContainerRemux = false;

        if (transcodingInfo != null)
        {
            isVideoDirect = transcodingInfo.IsVideoDirect;
            isAudioDirect = transcodingInfo.IsAudioDirect;

            // Remux means every stream is being copied without re-encoding (video AND audio direct).
            // Requiring both is what distinguishes a true remux from a session where audio is actively
            // being re-encoded, which must report as Transcode even though video itself is direct.
            if (transcodingInfo.IsVideoDirect && transcodingInfo.IsAudioDirect)
            {
                isContainerRemux = true;
            }
        }
        else if (rawPlayMethod == PlayMethod.DirectPlay)
        {
            isVideoDirect = true;
            isAudioDirect = true;
        }

        string playMethod;
        if (isContainerRemux) playMethod = "Remux";
        else if (rawPlayMethod == PlayMethod.DirectPlay) playMethod = "DirectPlay";
        else if (rawPlayMethod == PlayMethod.DirectStream) playMethod = "DirectStream";
        else if (rawPlayMethod == PlayMethod.Transcode || transcodingInfo != null) playMethod = "Transcode";
        else playMethod = rawPlayMethod.HasValue ? rawPlayMethod.Value.ToString() : "Unavailable";

        var videoStatus = isVideoDirect.HasValue
            ? (isVideoDirect.Value ? "Video Direct" : "Video Transcoded")
            : (playMethod == "DirectPlay" ? "Video Direct" : "Video status unavailable");

        var audioStatus = isAudioDirect.HasValue
            ? (isAudioDirect.Value ? "Audio Direct" : "Audio Transcoded")
            : (playMethod == "DirectPlay" ? "Audio Direct" : "Audio status unavailable");

        return new TranscodeClassification
        {
            PlayMethod = playMethod,
            IsVideoDirect = isVideoDirect,
            IsAudioDirect = isAudioDirect,
            IsContainerRemux = isContainerRemux,
            VideoStatus = videoStatus,
            AudioStatus = audioStatus
        };
    }

    public static PlaybackEventRecord Map(
        NotificationEventType eventType,
        SessionInfo? session,
        BaseItem? item,
        long? playbackPositionTicks = null,
        bool isPaused = false,
        bool playedToCompletion = false)
    {
        var sessionId = session?.Id;
        var userId = session?.UserId.ToString();
        var username = session?.UserName;
        var clientName = session?.Client ?? "Unknown Client";
        var deviceName = session?.DeviceName ?? "Unknown Device";
        var appVersion = session?.ApplicationVersion ?? string.Empty;

        // Fallback internal key if PlaySessionId is absent
        var internalKey = !string.IsNullOrEmpty(sessionId)
            ? sessionId
            : $"fallback:{userId ?? "anon"}:{item?.Id.ToString() ?? "unknown"}:{clientName}";

        var mediaTitle = item?.Name ?? "Unknown Title";
        string? seriesName = null;
        BaseItem? posterItem = item;
        int? seasonNumber = null;
        int? episodeNumber = null;
        int? productionYear = item?.ProductionYear;
        var itemType = item?.GetType().Name ?? "Unknown";
        var communityRating = item?.CommunityRating;
        var criticRating = item?.CriticRating;
        var officialRating = item?.OfficialRating;
        IReadOnlyList<string> genres = item?.Genres ?? Array.Empty<string>();
        string? imdbId = null;

        if (item?.ProviderIds != null &&
            item.ProviderIds.TryGetValue("Imdb", out var mappedImdbId) &&
            !string.IsNullOrWhiteSpace(mappedImdbId) &&
            System.Text.RegularExpressions.Regex.IsMatch(mappedImdbId, "^tt[0-9]{7,10}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            imdbId = mappedImdbId.ToLowerInvariant();
        }

        if (item is Episode episode)
        {
            seriesName = episode.SeriesName;
            seasonNumber = episode.ParentIndexNumber;
            episodeNumber = episode.IndexNumber;
            try
            {
                // Episode Primary images are usually landscape stills. Prefer the parent
                // series' portrait Primary image so Discord renders the same poster shape as
                // movies, while retaining the episode image as a safe fallback.
                posterItem = episode.Series ?? item;
            }
            catch
            {
                posterItem = item;
            }
        }
        else if (item != null)
        {
            seasonNumber = item.ParentIndexNumber;
            episodeNumber = item.IndexNumber;
        }

        // Position and duration calculation
        var positionTicks = playbackPositionTicks ?? session?.PlayState?.PositionTicks ?? 0;
        var position = TimeSpan.FromTicks(Math.Max(0, positionTicks));

        TimeSpan? totalDuration = null;
        if (item?.RunTimeTicks.HasValue == true && item.RunTimeTicks.Value > 0)
        {
            totalDuration = TimeSpan.FromTicks(item.RunTimeTicks.Value);
        }

        int? percentage = null;
        if (totalDuration.HasValue && totalDuration.Value.TotalSeconds > 0)
        {
            var pct = (int)Math.Clamp(Math.Round(position.TotalSeconds / totalDuration.Value.TotalSeconds * 100.0), 0, 100);
            percentage = pct;
        }

        // Stream telemetry extraction
        string? videoCodec = null;
        string? audioCodec = null;
        string? sourceContainer = item?.Container;
        string? outputContainer = null;
        string? resolution = null;
        string? dynamicRange = null;
        string? frameRate = null;
        string? transcodeSpeed = null;
        string? audioChannels = null;
        string? audioLanguage = null;
        string? subtitleLanguage = null;
        long? bitrate = null;
        string? transcodeEngine = null;
        var rawTranscodeReasons = new List<string>();

        var tInfo = session?.TranscodingInfo;
        var rawPlayMethod = session?.PlayState?.PlayMethod;

        if (tInfo != null)
        {
            outputContainer = tInfo.Container;
            if (!string.IsNullOrEmpty(tInfo.VideoCodec)) videoCodec = tInfo.VideoCodec;
            if (!string.IsNullOrEmpty(tInfo.AudioCodec)) audioCodec = tInfo.AudioCodec;
            if (tInfo.Bitrate.HasValue && tInfo.Bitrate.Value > 0) bitrate = tInfo.Bitrate.Value;

            if (tInfo.Width.HasValue && tInfo.Height.HasValue)
            {
                resolution = $"{tInfo.Width.Value}x{tInfo.Height.Value}";
            }

            if (tInfo.Framerate.HasValue && tInfo.Framerate.Value > 0)
            {
                // Jellyfin's TranscodingInfo.Framerate is encoder throughput, not the
                // source video's frame rate. Keep it separate so values such as 336 fps
                // are not presented as media metadata.
                transcodeSpeed = string.Format(CultureInfo.InvariantCulture, "{0:F0} fps", tInfo.Framerate.Value);
            }

            if (tInfo.AudioChannels.HasValue)
            {
                audioChannels = FormatAudioChannels(tInfo.AudioChannels.Value);
            }

            var hwType = tInfo.HardwareAccelerationType.ToString();
            if (!string.IsNullOrWhiteSpace(hwType) && !hwType.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                transcodeEngine = hwType;
            }

            var reasonsStr = tInfo.TranscodeReasons.ToString();
            if (!string.IsNullOrWhiteSpace(reasonsStr) && !reasonsStr.Equals("0", StringComparison.OrdinalIgnoreCase) && !reasonsStr.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                var split = reasonsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                rawTranscodeReasons.AddRange(split);
            }
        }

        var classification = ClassifyPlayback(tInfo, rawPlayMethod);
        var playMethod = classification.PlayMethod;
        var isVideoDirect = classification.IsVideoDirect;
        var isAudioDirect = classification.IsAudioDirect;
        var isContainerRemux = classification.IsContainerRemux;
        var videoStatus = classification.VideoStatus;
        var audioStatus = classification.AudioStatus;

        // Map truthful "Why" transcode reasons
        var transcodeReasonsWhy = MapTranscodeReasons(rawTranscodeReasons);

        // Primary image, if cached locally -- absent for a remote-only image, an item with no
        // image at all, or an item type that doesn't support GetImageInfo. Never lets a poster
        // lookup failure break the rest of the event mapping.
        string? primaryImagePath = null;
        try
        {
            primaryImagePath = posterItem?.GetImageInfo(ImageType.Primary, 0)?.Path;
        }
        catch
        {
            primaryImagePath = null;
        }

        // Fallback: extract from item media streams if not populated by transcode info
        IReadOnlyList<MediaStream>? streams = null;
        try
        {
            streams = item?.GetMediaStreams();
        }
        catch
        {
            streams = null;
        }

        if (streams != null)
        {
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream != null)
            {
                if (string.IsNullOrEmpty(videoCodec)) videoCodec = videoStream.Codec;
                if (string.IsNullOrEmpty(resolution) && videoStream.Width.HasValue && videoStream.Height.HasValue)
                {
                    resolution = $"{videoStream.Width.Value}x{videoStream.Height.Value}";
                }
                dynamicRange = FormatDynamicRange(videoStream);
                if (videoStream.RealFrameRate.HasValue && videoStream.RealFrameRate.Value > 0)
                {
                    frameRate = string.Format(CultureInfo.InvariantCulture, "{0:0.##} fps", videoStream.RealFrameRate.Value);
                }
                else if (videoStream.AverageFrameRate.HasValue && videoStream.AverageFrameRate.Value > 0)
                {
                    frameRate = string.Format(CultureInfo.InvariantCulture, "{0:0.##} fps", videoStream.AverageFrameRate.Value);
                }
            }

            var audioStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            if (audioStream != null)
            {
                if (string.IsNullOrEmpty(audioCodec)) audioCodec = audioStream.Codec;
                if (string.IsNullOrEmpty(audioChannels) && audioStream.Channels.HasValue)
                {
                    audioChannels = FormatAudioChannels(audioStream.Channels.Value);
                }
                if (string.IsNullOrEmpty(audioLanguage)) audioLanguage = audioStream.Language;
            }

            var subStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle);
            if (subStream != null)
            {
                subtitleLanguage = subStream.Language;
            }

            if (bitrate == null && videoStream?.BitRate.HasValue == true)
            {
                bitrate = videoStream.BitRate.Value;
            }
        }

        return new PlaybackEventRecord
        {
            InternalSessionKey = internalKey,
            EventType = eventType,
            Timestamp = DateTimeOffset.UtcNow,
            MediaTitle = mediaTitle,
            SeriesName = seriesName,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            ProductionYear = productionYear,
            ItemType = itemType,
            CommunityRating = communityRating,
            CriticRating = criticRating,
            OfficialRating = officialRating,
            Genres = genres,
            ImdbId = imdbId,
            UserId = userId,
            Username = username,
            ClientName = clientName,
            DeviceName = deviceName,
            ApplicationVersion = appVersion,
            PlayMethod = playMethod,
            IsPaused = isPaused,
            Position = position,
            TotalDuration = totalDuration,
            PlaybackPercentage = percentage,
            IsVideoDirect = isVideoDirect,
            IsAudioDirect = isAudioDirect,
            IsContainerRemux = isContainerRemux,
            VideoStatus = videoStatus,
            AudioStatus = audioStatus,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            SourceContainer = sourceContainer,
            Container = outputContainer ?? sourceContainer,
            Resolution = resolution,
            DynamicRange = dynamicRange,
            FrameRate = frameRate,
            TranscodeSpeed = transcodeSpeed,
            AudioChannels = audioChannels,
            AudioLanguage = audioLanguage,
            SubtitleLanguage = subtitleLanguage,
            Bitrate = bitrate,
            TranscodeEngine = transcodeEngine,
            TranscodeReasons = rawTranscodeReasons,
            TranscodeReasonsWhy = transcodeReasonsWhy,
            PlayedToCompletion = playedToCompletion,
            PrimaryImagePath = primaryImagePath
        };
    }

    private static string FormatAudioChannels(int channels)
    {
        return channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{channels}ch"
        };
    }

    private static string? FormatDynamicRange(MediaStream videoStream)
    {
        var dvProfile = Convert.ToString(videoStream.DvProfile, CultureInfo.InvariantCulture);
        var dvLevel = Convert.ToString(videoStream.DvLevel, CultureInfo.InvariantCulture);
        var descriptor = string.Join(
            ' ',
            new[]
            {
                videoStream.VideoRangeType.ToString(),
                videoStream.VideoRange.ToString(),
                videoStream.VideoDoViTitle,
                videoStream.DisplayTitle,
                videoStream.Title,
                videoStream.Profile,
                string.IsNullOrWhiteSpace(dvProfile) ? null : "DOVI",
                videoStream.RpuPresentFlag == 1 ? "DOVI RPU" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (descriptor.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
            || descriptor.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase)
            || descriptor.Contains("DVHE", StringComparison.OrdinalIgnoreCase)
            || descriptor.Contains("DVAV", StringComparison.OrdinalIgnoreCase)
            || descriptor.Contains("DVA1", StringComparison.OrdinalIgnoreCase))
        {
            var profile = dvProfile;
            var level = dvLevel;
            var profileMatch = System.Text.RegularExpressions.Regex.Match(descriptor, "(?:Profile|\\bP)\\s*(\\d+)(?:\\.(\\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(profile) && profileMatch.Success)
            {
                profile = profileMatch.Groups[1].Value;
            }
            if (string.IsNullOrWhiteSpace(level) && profileMatch.Success && profileMatch.Groups[2].Success) level = profileMatch.Groups[2].Value;

            var layer = descriptor.Contains("FEL", StringComparison.OrdinalIgnoreCase)
                ? "FEL"
                : descriptor.Contains("MEL", StringComparison.OrdinalIgnoreCase)
                    ? "MEL"
                    : videoStream.ElPresentFlag == 1 || descriptor.Contains("WithEL", StringComparison.OrdinalIgnoreCase) ? "EL" : null;
            var baseRange = descriptor.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)
                || descriptor.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase)
                || descriptor.Contains("HDR10 PLUS", StringComparison.OrdinalIgnoreCase)
                    ? "HDR10+"
                    : descriptor.Contains("HDR10", StringComparison.OrdinalIgnoreCase) ? "HDR10" : null;
            var details = string.Join(" · ", new[] { layer, baseRange }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return "Dolby Vision"
                + (!string.IsNullOrWhiteSpace(profile) ? $" Profile {profile}{(!string.IsNullOrWhiteSpace(level) ? $".{level}" : string.Empty)}" : string.Empty)
                + (!string.IsNullOrWhiteSpace(details) ? $" ({details})" : string.Empty);
        }

        if (descriptor.Contains("HDR10+", StringComparison.OrdinalIgnoreCase)
            || descriptor.Contains("HDR10PLUS", StringComparison.OrdinalIgnoreCase)
            || descriptor.Contains("HDR10 PLUS", StringComparison.OrdinalIgnoreCase))
        {
            return "HDR10+";
        }

        if (descriptor.Contains("HDR10", StringComparison.OrdinalIgnoreCase)) return "HDR10";
        if (descriptor.Contains("HLG", StringComparison.OrdinalIgnoreCase)) return "HLG";
        if (descriptor.Contains("HDR", StringComparison.OrdinalIgnoreCase)) return "HDR";
        if (descriptor.Contains("SDR", StringComparison.OrdinalIgnoreCase)) return "SDR";
        return null;
    }

    public static string MapTranscodeReasons(IReadOnlyList<string> reasons)
    {
        if (reasons == null || reasons.Count == 0)
        {
            return "Reason not reported by server";
        }

        var mapped = new List<string>(reasons.Count);
        foreach (var r in reasons)
        {
            var friendly = r switch
            {
                "ContainerNotSupported" => "Container unsupported",
                "VideoCodecNotSupported" => "Video codec unsupported",
                "AudioCodecNotSupported" => "Audio codec unsupported",
                "SubtitleCodecNotSupported" => "Subtitle incompatibility",
                "AudioIsExternal" => "External audio stream",
                "SecondaryAudioNotSupported" => "Secondary audio unsupported",
                "VideoProfileNotSupported" => "Video profile unsupported",
                "VideoLevelNotSupported" => "Video level unsupported",
                "VideoResolutionNotSupported" => "Resolution unsupported",
                "VideoBitDepthNotSupported" => "Bit depth unsupported",
                "VideoFramerateNotSupported" => "Frame rate unsupported",
                "RefFramesNotSupported" => "Reference frames unsupported",
                "AnamorphicVideoNotSupported" => "Anamorphic video unsupported",
                "InterlacedVideoNotSupported" => "Interlaced video unsupported",
                "AudioChannelsNotSupported" => "Audio channel limit",
                "AudioProfileNotSupported" => "Audio profile unsupported",
                "AudioSampleRateNotSupported" => "Audio sample rate unsupported",
                "AudioBitDepthNotSupported" => "Audio bit depth unsupported",
                "ContainerBitrateExceedsLimit" => "Container bitrate limit exceeded",
                "VideoBitrateNotSupported" => "Video bitrate limit exceeded",
                "AudioBitrateNotSupported" => "Audio bitrate limit exceeded",
                "UnknownVideoStreamInfo" => "Unknown video stream info",
                "UnknownAudioStreamInfo" => "Unknown audio stream info",
                "DirectPlayError" => "Direct play error",
                "VideoRangeTypeNotSupported" => "Video range/HDR incompatibility",
                _ => System.Text.RegularExpressions.Regex.Replace(r, "(\\B[A-Z])", " $1")
            };
            mapped.Add(friendly);
        }

        return string.Join(", ", mapped);
    }
}

