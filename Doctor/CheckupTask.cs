using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LibraryDoctor.Doctor;

/// <summary>Weekly checkup (Dashboard > Scheduled Tasks > Library Doctor).</summary>
public sealed class CheckupTask : IScheduledTask
{
    private readonly Examiner _examiner;

    public CheckupTask(Examiner examiner)
    {
        _examiner = examiner;
    }

    public string Name => "Library checkup";

    public string Key => "LibraryDoctorCheckup";

    public string Description => "Looks for unreadable files, missing episodes, duplicates, bad matches, missing artwork and files that will need converting.";

    public string Category => "Library Doctor";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _examiner.RunAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(30)).Ticks
        };
    }
}
