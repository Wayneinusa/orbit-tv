using System.Text.Json;

namespace SuperiorIptv;

public sealed class XtreamAccount
{
    public string Status = "";
    public DateTime? Expires;
    public int ActiveConnections;
    public int MaxConnections;
    public string TimeZone = "";
}

public sealed class XtreamVodInfo
{
    public string Plot = "";
    public string Year = "";
    public string Genre = "";
    public string Duration = "";
    public string Rating = "";
}

/// <summary>Talks to an Xtream Codes "player_api.php" server.</summary>
public sealed class XtreamClient
{
    public string Server { get; }
    public string Username { get; }
    public string Password { get; }
    public string UserAgent { get; }
    /// <summary>"ts" or "m3u8"</summary>
    public string LiveFormat { get; }

    public XtreamClient(string server, string username, string password, string userAgent = "", string liveFormat = "ts")
    {
        Server = Normalize(server);
        Username = username ?? "";
        Password = password ?? "";
        UserAgent = userAgent ?? "";
        LiveFormat = string.IsNullOrEmpty(liveFormat) ? "ts" : liveFormat;
    }

    public static string Normalize(string server)
    {
        string text = (server ?? "").Trim();
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text;
        }
        // People often paste the full player_api / get.php link.
        foreach (string marker in new[] { "/player_api.php", "/get.php", "/xmltv.php" })
        {
            int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) text = text[..index];
        }
        return text.TrimEnd('/');
    }

    private static string E(string text) => Uri.EscapeDataString(text ?? "");

    public string ApiUrl(string action, params (string Key, string Value)[] extra)
    {
        string url = $"{Server}/player_api.php?username={E(Username)}&password={E(Password)}";
        if (!string.IsNullOrEmpty(action)) url += "&action=" + E(action);
        foreach (var (key, value) in extra) url += $"&{E(key)}={E(value)}";
        return url;
    }

    public string XmltvUrl => $"{Server}/xmltv.php?username={E(Username)}&password={E(Password)}";
    public string LiveUrl(int streamId) => $"{Server}/live/{E(Username)}/{E(Password)}/{streamId}.{LiveFormat}";
    public string MovieUrl(int streamId, string container) => $"{Server}/movie/{E(Username)}/{E(Password)}/{streamId}.{(string.IsNullOrEmpty(container) ? "mp4" : container)}";
    public string EpisodeUrl(string episodeId, string container) => $"{Server}/series/{E(Username)}/{E(Password)}/{episodeId}.{(string.IsNullOrEmpty(container) ? "mp4" : container)}";

    /// <summary>Catch-up ("timeshift") address. <paramref name="startText"/> must already be in the server's time zone.</summary>
    public string TimeshiftUrl(int streamId, string startText, int minutes) =>
        $"{Server}/timeshift/{E(Username)}/{E(Password)}/{minutes}/{startText}/{streamId}.ts";

    // ---- Loose JSON helpers: Xtream servers send numbers as strings and the other way round.

    public static string Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => ""
        };
    }

    public static int? Int(JsonElement element, string name)
    {
        string text = Str(element, name);
        if (text.Length == 0) return null;
        if (int.TryParse(text, out int number)) return number;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double real)) return (int)real;
        return null;
    }

    private static JsonElement Child(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)) return value;
        return default;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) yield return item;
        }
    }

    private async Task<JsonDocument> FetchAsync(string action, CancellationToken token, params (string, string)[] extra)
    {
        string text = await Net.GetStringAsync(ApiUrl(action, extra), UserAgent, TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new UserException("The server did not return valid data. Check the address, username and password.");
        }
    }

    public async Task<XtreamAccount> AccountAsync(CancellationToken token = default)
    {
        using JsonDocument document = await FetchAsync(null, token).ConfigureAwait(false);
        JsonElement user = Child(document.RootElement, "user_info");
        if (user.ValueKind != JsonValueKind.Object) throw new UserException("Login failed. Check the username and password.");
        if (Int(user, "auth") == 0) throw new UserException("Login failed. Check the username and password.");
        var account = new XtreamAccount
        {
            Status = Str(user, "status"),
            ActiveConnections = Int(user, "active_cons") ?? 0,
            MaxConnections = Int(user, "max_connections") ?? 0,
            TimeZone = Str(Child(document.RootElement, "server_info"), "timezone")
        };
        if (long.TryParse(Str(user, "exp_date"), out long seconds) && seconds > 0)
        {
            account.Expires = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        return account;
    }

    private async Task<(Dictionary<string, string> Names, Dictionary<string, int> Order)> CategoriesAsync(string action, CancellationToken token)
    {
        var names = new Dictionary<string, string>();
        var order = new Dictionary<string, int>();
        try
        {
            using JsonDocument document = await FetchAsync(action, token).ConfigureAwait(false);
            int index = 0;
            foreach (JsonElement item in Items(document.RootElement))
            {
                string id = Str(item, "category_id");
                names[id] = Str(item, "category_name");
                order[id] = index++;
            }
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // A provider without categories still has streams.
        }
        return (names, order);
    }

    /// <summary>Downloads the live channel, movie and series lists.</summary>
    public async Task<(PlaylistContent Content, string TimeZone)> LoadAllAsync(string playlistId, CancellationToken token = default)
    {
        XtreamAccount account = await AccountAsync(token).ConfigureAwait(false);
        string prefix = playlistId.Length > 8 ? playlistId[..8] : playlistId;
        var content = new PlaylistContent();
        content.EpgUrls.Add(XmltvUrl);

        // Live
        var liveCategories = await CategoriesAsync("get_live_categories", token).ConfigureAwait(false);
        var channels = new List<(int Order, int Index, Channel Channel)>();
        using (JsonDocument live = await FetchAsync("get_live_streams", token).ConfigureAwait(false))
        {
            int index = 0;
            foreach (JsonElement item in Items(live.RootElement))
            {
                index++;
                int? streamId = Int(item, "stream_id");
                if (streamId == null) continue;
                string categoryId = Str(item, "category_id");
                bool hasArchive = (Int(item, "tv_archive") ?? 0) == 1;
                int archiveDays = Int(item, "tv_archive_duration") ?? 0;
                var channel = new Channel
                {
                    Id = $"{prefix}:l:{streamId}",
                    PlaylistId = playlistId,
                    Number = index,
                    Name = Str(item, "name"),
                    Group = liveCategories.Names.GetValueOrDefault(categoryId, ChannelGroups.Uncategorized),
                    Logo = Str(item, "stream_icon"),
                    Url = LiveUrl(streamId.Value),
                    EpgId = Str(item, "epg_channel_id").ToLowerInvariant(),
                    Catchup = hasArchive ? new CatchupInfo { Mode = CatchupMode.Xtream, Days = Math.Max(1, archiveDays) } : null,
                    XtreamId = streamId,
                    UserAgent = UserAgent
                };
                channels.Add((liveCategories.Order.GetValueOrDefault(categoryId, int.MaxValue), index, channel));
            }
        }
        // Keep the provider's category order, then the provider's own order inside each category.
        content.Channels = channels.OrderBy(c => c.Order).ThenBy(c => c.Index).Select(c => c.Channel).ToList();
        for (int i = 0; i < content.Channels.Count; i++) content.Channels[i].Number = i + 1;

        // Movies (a provider may not offer any; that is not an error)
        var vodCategories = await CategoriesAsync("get_vod_categories", token).ConfigureAwait(false);
        try
        {
            using JsonDocument vod = await FetchAsync("get_vod_streams", token).ConfigureAwait(false);
            foreach (JsonElement item in Items(vod.RootElement))
            {
                int? streamId = Int(item, "stream_id");
                if (streamId == null) continue;
                content.Movies.Add(new Movie
                {
                    Id = $"{prefix}:m:{streamId}",
                    PlaylistId = playlistId,
                    Name = Str(item, "name"),
                    Group = vodCategories.Names.GetValueOrDefault(Str(item, "category_id"), ChannelGroups.Uncategorized),
                    Poster = Str(item, "stream_icon"),
                    Url = MovieUrl(streamId.Value, Str(item, "container_extension")),
                    Rating = Str(item, "rating"),
                    Year = Str(item, "year"),
                    XtreamId = streamId
                });
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        // Series
        var seriesCategories = await CategoriesAsync("get_series_categories", token).ConfigureAwait(false);
        try
        {
            using JsonDocument shows = await FetchAsync("get_series", token).ConfigureAwait(false);
            foreach (JsonElement item in Items(shows.RootElement))
            {
                int? seriesId = Int(item, "series_id");
                if (seriesId == null) continue;
                string released = Str(item, "releaseDate");
                content.Series.Add(new Series
                {
                    Id = $"{prefix}:s:{seriesId}",
                    PlaylistId = playlistId,
                    Name = Str(item, "name"),
                    Group = seriesCategories.Names.GetValueOrDefault(Str(item, "category_id"), ChannelGroups.Uncategorized),
                    Poster = Str(item, "cover"),
                    Plot = Str(item, "plot"),
                    Rating = Str(item, "rating"),
                    Year = released.Length >= 4 ? released[..4] : "",
                    XtreamId = seriesId
                });
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }

        return (content, account.TimeZone);
    }

    public async Task<List<Episode>> EpisodesAsync(int seriesId, string idPrefix, CancellationToken token = default)
    {
        using JsonDocument document = await FetchAsync("get_series_info", token, ("series_id", seriesId.ToString())).ConfigureAwait(false);
        var result = new List<Episode>();

        void Add(JsonElement item, int fallbackSeason)
        {
            string episodeId = Str(item, "id");
            if (episodeId.Length == 0) return;
            int number = Int(item, "episode_num") ?? (result.Count + 1);
            string title = Str(item, "title");
            if (title.Length == 0) title = "Episode " + number;
            JsonElement info = Child(item, "info");
            result.Add(new Episode
            {
                Id = $"{idPrefix}:e:{episodeId}",
                Season = Int(item, "season") ?? fallbackSeason,
                Number = number,
                Title = title,
                Url = EpisodeUrl(episodeId, Str(item, "container_extension")),
                Plot = Str(info, "plot"),
                Image = Str(info, "movie_image")
            });
        }

        JsonElement episodes = Child(document.RootElement, "episodes");
        if (episodes.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty season in episodes.EnumerateObject())
            {
                int.TryParse(season.Name, out int seasonNumber);
                foreach (JsonElement item in Items(season.Value)) Add(item, seasonNumber == 0 ? 1 : seasonNumber);
            }
        }
        else
        {
            // Some panels return an array of seasons instead of an object.
            int index = 0;
            foreach (JsonElement list in Items(episodes))
            {
                index++;
                foreach (JsonElement item in Items(list)) Add(item, index);
            }
        }
        return result.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
    }

    public async Task<XtreamVodInfo> MovieInfoAsync(int streamId, CancellationToken token = default)
    {
        using JsonDocument document = await FetchAsync("get_vod_info", token, ("vod_id", streamId.ToString())).ConfigureAwait(false);
        JsonElement info = Child(document.RootElement, "info");
        string plot = Str(info, "plot");
        string released = Str(info, "releasedate");
        return new XtreamVodInfo
        {
            Plot = plot.Length == 0 ? Str(info, "description") : plot,
            Year = released.Length >= 4 ? released[..4] : "",
            Genre = Str(info, "genre"),
            Duration = Str(info, "duration"),
            Rating = Str(info, "rating")
        };
    }
}
