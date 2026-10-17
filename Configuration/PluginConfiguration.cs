using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryDoctor.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Which checks run.
    public bool CheckUnreadable { get; set; } = true;

    public bool CheckMissingEpisodes { get; set; } = true;

    public bool CheckDuplicates { get; set; } = true;

    public bool CheckMatches { get; set; } = true;

    public bool CheckPlayback { get; set; } = true;

    public bool CheckArtwork { get; set; } = true;

    // What your TV can play without converting (defaults fit Apple TV).
    public string PlayableVideoCodecs { get; set; } = "h264,hevc";

    public string PlayableAudioCodecs { get; set; } = "aac,ac3,eac3,mp3,alac,flac";

    public string PlayableContainers { get; set; } = "mp4,m4v,mov,mkv";

    public bool FlagImageSubtitles { get; set; } = true;

    // Show paths the way the NAS sees them (Jellyfin runs in Docker, so it sees different paths).
    public string PathInJellyfin { get; set; } = string.Empty;

    public string PathOnNas { get; set; } = string.Empty;

    /// <summary>Findings you chose to ignore ("check:itemId").</summary>
    public List<string> Ignored { get; set; } = new();
}
