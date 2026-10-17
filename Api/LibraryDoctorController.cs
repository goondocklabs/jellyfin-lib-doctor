using Jellyfin.Plugin.LibraryDoctor.Doctor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryDoctor.Api;

/// <summary>Used by the Library Doctor page (admins only).</summary>
[ApiController]
[Route("LibraryDoctor")]
[Authorize(Policy = "RequiresElevation")]
public class LibraryDoctorController : ControllerBase
{
    private readonly Examiner _examiner;

    public LibraryDoctorController(Examiner examiner)
    {
        _examiner = examiner;
    }

    /// <summary>The last report, plus whether a checkup is running.</summary>
    [HttpGet("Report")]
    public ActionResult<object> Report()
        => Ok(new { running = _examiner.Running, progress = _examiner.Progress, report = _examiner.LastReport() });

    [HttpPost("Run")]
    public ActionResult<object> Run() => Ok(new { started = _examiner.Start() });

    [HttpPost("Ignore")]
    public ActionResult Ignore([FromQuery] string key)
    {
        _examiner.Ignore(key);
        return NoContent();
    }

    [HttpPost("ClearIgnored")]
    public ActionResult ClearIgnored()
    {
        _examiner.ClearIgnored();
        return NoContent();
    }
}
