using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryDoctor.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryDoctor.Doctor;

/// <summary>One thing the checkup found.</summary>
public sealed class Finding
{
    public string Check { get; set; } = string.Empty;

    public string Severity { get; set; } = "warning"; // problem, warning, info

    public string ItemId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Fix { get; set; } = string.Empty;

    public string Key => Check + ":" + ItemId;
}

/// <summary>The last checkup.</summary>
public sealed class Report
{
    public DateTime RanAt { get; set; }

    public double Seconds { get; set; }

    public int ItemsChecked { get; set; }

    public List<Finding> Findings { get; set; } = new();
}

/// <summary>Runs the checkup and keeps the last report on disk.</summary>
public sealed class Examiner
{
    private const int MaxPerCheck = 1500;

    private static readonly Regex YearRe = new(@"(?:^|[\s.\(\[_-])((?:19|20)\d\d)(?:$|[\s.\)\]_-])", RegexOptions.Compiled);
    private static readonly Regex WordRe = new(@"[a-z0-9]{3,}", RegexOptions.Compiled);
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "1080p", "720p", "2160p", "480p", "bluray", "web", "webrip", "dl", "hdr", "x264", "x265", "hevc", "h264", "remux", "dvd", "dvdrip", "aac", "dts", "proper", "extended", "edition", "cut", "uhd"
    };

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _media;
    private readonly ILogger<Examiner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Examiner(ILibraryManager library, IMediaSourceManager media, ILogger<Examiner> logger)
    {
        _library = library;
        _media = media;
        _logger = logger;
    }

    public bool Running { get; private set; }

    public double Progress { get; private set; }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static string ReportPath => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "report.json");

    public Report? LastReport()
    {
        try
        {
            return File.Exists(ReportPath) ? JsonSerializer.Deserialize<Report>(File.ReadAllText(ReportPath)) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library Doctor: couldn't read the last report");
            return null;
        }
    }

    /// <summary>Start a checkup in the background (the page polls Status). False if one is already running.</summary>
    public bool Start()
    {
        if (Running)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library Doctor: checkup failed");
            }
        });
        return true;
    }

    public async Task<Report> RunAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Running = true;
        Progress = 0;
        var started = DateTime.UtcNow;
        try
        {
            var cfg = Config;
            var report = new Report { RanAt = started };
            void Step(double pct)
            {
                Progress = pct;
                progress?.Report(pct);
            }

            var videos = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video },
                IsVirtualItem = false,
                Recursive = true
            }).Where(i => !string.IsNullOrEmpty(i.Path) && i.OwnerId == Guid.Empty).ToList(); // skip extras and alternate versions
            var titles = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicVideo },
                IsVirtualItem = false,
                Recursive = true
            }).ToList();
            report.ItemsChecked = videos.Count;
            Step(5);

            var owner = Owner();
            var add = new Action<Finding>(f =>
            {
                if (report.Findings.Count(x => x.Check == f.Check) < MaxPerCheck)
                {
                    f.Path = NasPath(f.Path);
                    report.Findings.Add(f);
                }
            });

            for (var i = 0; i < videos.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = videos[i];
                var readable = true;
                if (cfg.CheckUnreadable)
                {
                    readable = CheckFile(item, owner, add);
                }

                if (readable && (cfg.CheckPlayback || cfg.CheckUnreadable))
                {
                    CheckStreams(item, cfg, add);
                }

                if (i % 50 == 0)
                {
                    Step(5 + (70.0 * i / Math.Max(1, videos.Count)));
                    await Task.Yield();
                }
            }

            Step(75);
            if (cfg.CheckMissingEpisodes)
            {
                MissingEpisodes(videos.OfType<Episode>().ToList(), add);
            }

            Step(82);
            if (cfg.CheckDuplicates)
            {
                Duplicates(videos, add);
            }

            Step(90);
            if (cfg.CheckMatches)
            {
                Matches(titles, add);
            }

            if (cfg.CheckArtwork)
            {
                foreach (var t in titles.Where(t => !t.HasImage(ImageType.Primary, 0)))
                {
                    add(new Finding
                    {
                        Check = "artwork",
                        Severity = "info",
                        ItemId = t.Id.ToString("N"),
                        Title = Label(t),
                        Detail = "No poster. Open it, then Edit Images, or refresh its metadata.",
                        Path = t.Path ?? string.Empty
                    });
                }
            }

            var ignored = new HashSet<string>(cfg.Ignored);
            report.Findings = report.Findings.Where(f => !ignored.Contains(f.Key)).ToList();
            report.Seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);
            Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
            await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(report), ct).ConfigureAwait(false);
            Step(100);
            _logger.LogInformation("Library Doctor: checked {Count} files in {Seconds}s, {Findings} findings", report.ItemsChecked, report.Seconds, report.Findings.Count);
            return report;
        }
        finally
        {
            Running = false;
            _gate.Release();
        }
    }

    /// <summary>Remove one finding from the saved report and remember to skip it from now on.</summary>
    public void Ignore(string key)
    {
        var cfg = Config;
        if (!cfg.Ignored.Contains(key))
        {
            cfg.Ignored.Add(key);
            Plugin.Instance!.SaveConfiguration();
        }

        var report = LastReport();
        if (report is not null)
        {
            report.Findings.RemoveAll(f => f.Key == key);
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(report));
        }
    }

    public void ClearIgnored()
    {
        Config.Ignored.Clear();
        Plugin.Instance!.SaveConfiguration();
    }

    // ---------- checks ----------

    /// <summary>Can Jellyfin open the file? Returns false when it can't.</summary>
    private static bool CheckFile(BaseItem item, (string Uid, string Gid) owner, Action<Finding> add)
    {
        var path = item.Path!;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? problem = null;
        if (Directory.Exists(path))
        {
            try
            {
                _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
            }
            catch (UnauthorizedAccessException)
            {
                problem = "Permission denied: Jellyfin isn't allowed to open this folder.";
            }
        }
        else if (!File.Exists(path))
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            problem = dir is not null && Directory.Exists(dir) && !CanList(dir)
                ? "Permission denied: Jellyfin isn't allowed into this folder."
                : "The file is gone (moved, renamed or deleted). Rescan the library to clear it.";
            if (problem.StartsWith("The file", StringComparison.Ordinal))
            {
                add(Make(item, "unreadable", "warning", problem, path, string.Empty));
                return false;
            }
        }
        else
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                var b = new byte[1];
                _ = fs.Read(b, 0, 1);
            }
            catch (UnauthorizedAccessException)
            {
                problem = "Permission denied: Jellyfin isn't allowed to read this file.";
            }
            catch (IOException ex)
            {
                problem = "Can't read the file: " + ex.Message;
            }
        }

        if (problem is null)
        {
            return true;
        }

        var folder = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? path;
        var nas = NasPath(folder);
        var fix = problem.StartsWith("Permission", StringComparison.Ordinal) && owner.Uid.Length > 0
            ? $"sudo chown -R {owner.Uid}:{owner.Gid} \"{nas}\" && sudo chmod -R u+rwX \"{nas}\""
            : string.Empty;
        add(Make(item, "unreadable", "problem", problem, path, fix));
        return false;
    }

    private static bool CanList(string dir)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(dir).FirstOrDefault();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Media info: missing entirely (Jellyfin couldn't probe it), or formats the TV can't play directly.</summary>
    private void CheckStreams(BaseItem item, PluginConfiguration cfg, Action<Finding> add)
    {
        List<MediaStream> streams;
        try
        {
            streams = _media.GetMediaStreams(item.Id).ToList();
        }
        catch (Exception)
        {
            return;
        }

        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);
        if (video is null)
        {
            if (cfg.CheckUnreadable && Directory.Exists(item.Path!) == false)
            {
                add(Make(item, "unreadable", "warning", "Jellyfin has no media info for this file, so it may be damaged or still copying. Try Refresh Metadata on it; if it stays, check that it plays on a computer.", item.Path!, string.Empty));
            }

            return;
        }

        if (!cfg.CheckPlayback)
        {
            return;
        }

        var vOk = List(cfg.PlayableVideoCodecs);
        var aOk = List(cfg.PlayableAudioCodecs);
        var cOk = List(cfg.PlayableContainers);
        var reasons = new List<string>();
        var severity = "info";

        var vcodec = (video.Codec ?? string.Empty).ToLowerInvariant();
        if (vcodec.Length > 0 && !vOk.Contains(vcodec))
        {
            reasons.Add($"video is {Pretty(vcodec)}, so the picture is converted on the fly");
            severity = "warning";
        }
        else if (vcodec == "h264" && (video.BitDepth ?? 8) > 8)
        {
            reasons.Add("video is 10-bit H.264, which Apple TV can't decode, so the picture is converted on the fly");
            severity = "warning";
        }

        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audio.Count > 0 && !audio.Any(a => aOk.Contains((a.Codec ?? string.Empty).ToLowerInvariant())))
        {
            var main = audio.FirstOrDefault(a => a.IsDefault) ?? audio[0];
            reasons.Add($"audio is {Pretty(main.Codec ?? "unknown")}, so the sound is converted (light work)");
        }

        var containers = (item.Container ?? string.Empty).ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (containers.Length > 0 && !containers.Any(cOk.Contains))
        {
            reasons.Add($"the file type is .{containers[0]}, so it is repackaged as it plays (quick, but slower to start)");
        }

        if (cfg.FlagImageSubtitles && streams.Any(s => s.Type == MediaStreamType.Subtitle && !s.IsTextSubtitleStream && !s.IsExternal))
        {
            reasons.Add("it has picture-based subtitles (PGS/VobSub); turning them on forces the whole video to convert");
        }

        if (reasons.Count == 0)
        {
            return;
        }

        var size = video.Width is int w && video.Height is int h ? $"{w}x{h} " : string.Empty;
        add(Make(item, "playback", severity, Cap(string.Join("; ", reasons)) + ".", item.Path!, string.Empty, $"{size}{Pretty(vcodec)}"));
    }

    private static void MissingEpisodes(List<Episode> episodes, Action<Finding> add)
    {
        foreach (var show in episodes.Where(e => e.SeriesId != Guid.Empty).GroupBy(e => e.SeriesId))
        {
            var seasons = show.Where(e => (e.ParentIndexNumber ?? 0) > 0 && e.IndexNumber is not null)
                .GroupBy(e => e.ParentIndexNumber!.Value).ToDictionary(g => g.Key, g => g.ToList());
            if (seasons.Count == 0)
            {
                continue;
            }

            var lines = new List<string>();
            var maxSeason = seasons.Keys.Max();
            for (var s = 1; s <= maxSeason; s++)
            {
                if (!seasons.TryGetValue(s, out var eps))
                {
                    lines.Add($"Season {s}: missing entirely");
                    continue;
                }

                var have = new HashSet<int>();
                foreach (var e in eps)
                {
                    var end = e.IndexNumberEnd ?? e.IndexNumber!.Value;
                    for (var n = e.IndexNumber!.Value; n <= end && n < e.IndexNumber.Value + 10; n++)
                    {
                        have.Add(n);
                    }
                }

                var missing = Enumerable.Range(1, have.Max()).Where(n => !have.Contains(n)).ToList();
                if (missing.Count > 0)
                {
                    lines.Add($"Season {s}: missing episode{(missing.Count > 1 ? "s" : string.Empty)} {Ranges(missing)}");
                }
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var first = show.First();
            var folder = first.Series?.Path ?? System.IO.Path.GetDirectoryName(first.Path ?? string.Empty) ?? string.Empty;
            add(new Finding
            {
                Check = "episodes",
                Severity = "warning",
                ItemId = show.Key.ToString("N"),
                Title = first.SeriesName ?? "Unknown show",
                Detail = string.Join(". ", lines) + ". (Only counts gaps before the last episode you have.)",
                Path = folder
            });
        }
    }

    private static void Duplicates(List<BaseItem> videos, Action<Finding> add)
    {
        // Movies matched to the same movie.
        foreach (var g in videos.Where(v => v is MediaBrowser.Controller.Entities.Movies.Movie)
                     .Select(v => (Item: v, Id: Provider(v, "Tmdb") ?? Provider(v, "Imdb")))
                     .Where(x => x.Id is not null)
                     .GroupBy(x => x.Id!)
                     .Where(g => g.Count() > 1))
        {
            var items = g.Select(x => x.Item).ToList();
            add(new Finding
            {
                Check = "duplicates",
                Severity = "warning",
                ItemId = items[0].Id.ToString("N"),
                Title = Label(items[0]),
                Detail = $"{items.Count} copies in the library. Keep the best one, or put them in one folder so Jellyfin shows them as versions.",
                Path = string.Join("\n", items.Select(i => NasPath(i.Path ?? string.Empty)))
            });
        }

        // The same episode more than once.
        foreach (var g in videos.OfType<Episode>()
                     .Where(e => e.SeriesId != Guid.Empty && e.ParentIndexNumber is not null && e.IndexNumber is not null)
                     .GroupBy(e => (e.SeriesId, S: e.ParentIndexNumber!.Value, E: e.IndexNumber!.Value))
                     .Where(g => g.Count() > 1))
        {
            var items = g.ToList();
            add(new Finding
            {
                Check = "duplicates",
                Severity = "warning",
                ItemId = items[0].Id.ToString("N"),
                Title = $"{items[0].SeriesName} S{g.Key.S:00}E{g.Key.E:00}",
                Detail = $"{items.Count} files for the same episode.",
                Path = string.Join("\n", items.Select(i => NasPath(i.Path ?? string.Empty)))
            });
        }
    }

    private static void Matches(List<BaseItem> titles, Action<Finding> add)
    {
        foreach (var t in titles.Where(t => t is not MediaBrowser.Controller.Entities.MusicVideo))
        {
            var hasId = Provider(t, "Tmdb") is not null || Provider(t, "Imdb") is not null || Provider(t, "Tvdb") is not null;
            if (!hasId)
            {
                add(new Finding
                {
                    Check = "matches",
                    Severity = "warning",
                    ItemId = t.Id.ToString("N"),
                    Title = Label(t),
                    Detail = "Not matched to any movie or show online, so it has no description or ratings. Open it, then Identify.",
                    Path = t.Path ?? string.Empty
                });
                continue;
            }

            var name = NameFromPath(t);
            if (name.Length == 0)
            {
                continue;
            }

            var reasons = new List<string>();
            var m = YearRe.Match(name);
            if (m.Success && t.ProductionYear is int y && Math.Abs(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - y) > 1)
            {
                reasons.Add($"the file says {m.Groups[1].Value} but it's matched to a {y} title");
            }

            var fileWords = Words(m.Success ? name[..m.Index] : name);
            var titleWords = Words(t.Name + " " + (t.OriginalTitle ?? string.Empty));
            if (fileWords.Count > 0 && titleWords.Count > 0 && !fileWords.Overlaps(titleWords))
            {
                reasons.Add($"the file name (\"{name}\") doesn't share any words with the title");
            }

            if (reasons.Count > 0)
            {
                add(new Finding
                {
                    Check = "matches",
                    Severity = "warning",
                    ItemId = t.Id.ToString("N"),
                    Title = Label(t),
                    Detail = "Might be matched to the wrong title: " + string.Join("; ", reasons) + ". If it's wrong, open it, then Identify.",
                    Path = t.Path ?? string.Empty
                });
            }
        }
    }

    // ---------- helpers ----------

    private static Finding Make(BaseItem item, string check, string severity, string detail, string path, string fix, string? extra = null) => new()
    {
        Check = check,
        Severity = severity,
        ItemId = item.Id.ToString("N"),
        Title = Label(item) + (string.IsNullOrEmpty(extra) ? string.Empty : $" · {extra}"),
        Detail = detail,
        Path = path,
        Fix = fix
    };

    private static string Label(BaseItem item) => item switch
    {
        Episode e => $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00} {e.Name}",
        _ when item.ProductionYear is int y => $"{item.Name} ({y})",
        _ => item.Name ?? "Untitled"
    };

    private static string? Provider(BaseItem item, string key)
        => item.ProviderIds is not null && item.ProviderIds.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static string NameFromPath(BaseItem t)
    {
        var p = t.Path ?? string.Empty;
        if (p.Length == 0)
        {
            return string.Empty;
        }

        // Movie files: the file name; otherwise the folder name.
        var name = File.Exists(p) ? System.IO.Path.GetFileNameWithoutExtension(p) : System.IO.Path.GetFileName(p.TrimEnd('/', '\\'));
        if (t is MediaBrowser.Controller.Entities.Movies.Movie && File.Exists(p))
        {
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(p) ?? string.Empty);
            if (YearRe.IsMatch(folder) && !YearRe.IsMatch(name))
            {
                name = folder;
            }
        }

        return Regex.Replace(name, @"\[[^\]]*\]|\{[^}]*\}", " ").Trim();
    }

    private static HashSet<string> Words(string s)
        => WordRe.Matches(s.ToLowerInvariant()).Select(m => m.Value).Where(w => !Filler.Contains(w)).ToHashSet();

    private static HashSet<string> List(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToHashSet();

    private static string Ranges(List<int> n)
    {
        var parts = new List<string>();
        for (var i = 0; i < n.Count; i++)
        {
            var j = i;
            while (j + 1 < n.Count && n[j + 1] == n[j] + 1)
            {
                j++;
            }

            parts.Add(j > i ? $"{n[i]} to {n[j]}" : n[i].ToString(CultureInfo.InvariantCulture));
            i = j;
        }

        return string.Join(", ", parts);
    }

    private static string Pretty(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4 (DivX/Xvid)",
        "msmpeg4v3" => "DivX 3",
        "vc1" => "VC-1",
        "wmv3" => "WMV",
        "av1" => "AV1",
        "vp9" => "VP9",
        "dts" => "DTS",
        "truehd" => "Dolby TrueHD",
        "pcm_s16le" or "pcm_s24le" => "PCM",
        "" => "unknown",
        var c => c.ToUpperInvariant()
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string NasPath(string path)
    {
        var cfg = Config;
        var from = cfg.PathInJellyfin.Trim().TrimEnd('/');
        var to = cfg.PathOnNas.Trim().TrimEnd('/');
        if (from.Length == 0 || to.Length == 0)
        {
            return path;
        }

        return string.Join("\n", path.Split('\n').Select(p => p.StartsWith(from + "/", StringComparison.Ordinal) || p == from ? to + p[from.Length..] : p));
    }

    /// <summary>The user Jellyfin runs as, for the permission fix command.</summary>
    private static (string Uid, string Gid) Owner()
    {
        try
        {
            string uid = string.Empty, gid = string.Empty;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    uid = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                }
                else if (line.StartsWith("Gid:", StringComparison.Ordinal))
                {
                    gid = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                }
            }

            return (uid, gid);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }
}
