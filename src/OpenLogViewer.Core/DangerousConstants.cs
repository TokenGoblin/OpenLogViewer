namespace OpenLogViewer.Core;

/// <summary>
/// A tuning setting whose whole job is to stop the engine doing something, and
/// which an agent moving it unnoticed is exactly the failure this project exists
/// to guard against.
/// </summary>
public enum DangerousRole
{
    /// <summary>What stops the engine over-revving at all.</summary>
    RevLimiter,

    /// <summary>The RPM a launch or two-step control holds against, off idle.</summary>
    LaunchControlRpm,

    /// <summary>The ceiling on manifold pressure a boost controller is allowed to reach.</summary>
    BoostLimit,

    /// <summary>Cutting spark as a limiter's or a shift-cut's actual mechanism.</summary>
    IgnitionCut,

    /// <summary>Cutting fuel as a limiter's, an overrun's, or a shutdown's mechanism.</summary>
    FuelCut,
}

/// <summary>
/// Recognising the handful of tune settings that exist specifically to stop the
/// engine hurting itself, so a write to one of them can be held to a stricter
/// gate than an ordinary VE cell.
///
/// <para>
/// The shape here is the same one <see cref="ChannelRoles"/> uses for log
/// channels: a name means nothing on its own, so what a firmware calls a rev
/// limiter is matched by trying the spellings real MegaSquirt, Speeduino and
/// rusEFI INI files actually use — <c>SoftLimit</c>/<c>HardRevLim</c> on a
/// MegaSquirt, <c>launchTiming</c>/<c>hardRevLimit</c> on Speeduino,
/// <c>rpmHardLimit</c>/<c>boostCtrlMax</c> on rusEFI, and so on.
/// </para>
/// <para>
/// <b>This is a heuristic, not a certified list.</b> Nobody has catalogued
/// every dangerous-constant spelling across every firmware this application
/// reads, and a name that merely contains one of these fragments is flagged
/// whether or not it is really load-bearing — an unrelated "boostGaugeMax"
/// display setting matches "boostmax" and gets asked to confirm for no real
/// reason. That is the right side to be wrong on: a false positive costs an
/// agent one extra <c>confirmDangerous:true</c>, while a false negative is a
/// rev limiter moved with no warning at all. Defense in depth, not a
/// substitute for the RPM check and the magnitude limit sitting next to it.
/// </para>
/// </summary>
public static class DangerousConstants
{
    /// <summary>
    /// The role a named constant plays, or null when this firmware declares no
    /// such constant, or declares one that matches nothing here.
    ///
    /// Checked against the layout's own constants first — matching by name
    /// alone, with no such constant in the tune, would flag a typo the same as
    /// a real setting and refuse a write that <c>AgentSetSetting</c> was about
    /// to reject anyway for a better reason ("no such setting").
    /// </summary>
    public static DangerousRole? Find(TuneLayout layout, string constantName)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (string.IsNullOrWhiteSpace(constantName)) return null;

        if (!layout.Constants.Any(c => c.Name.Equals(constantName, StringComparison.OrdinalIgnoreCase)))
            return null;

        string simplified = ChannelRoles.Simplify(constantName);

        foreach (DangerousRole role in Enum.GetValues<DangerousRole>())
            if (Array.Exists(Aliases(role), alias => simplified.Contains(alias, StringComparison.Ordinal)))
                return role;

        return null;
    }

    /// <summary>Whether a named constant is one of the roles above, for a caller that does not need which.</summary>
    public static bool IsDangerous(TuneLayout layout, string constantName) => Find(layout, constantName) is not null;

    /// <summary>
    /// Name fragments, most of them substrings rather than whole names — a
    /// dangerous constant's spelling varies by firmware far more than a log
    /// channel's does, so this is matched loosely on purpose.
    /// </summary>
    private static string[] Aliases(DangerousRole role) => role switch
    {
        // "kindoflimiting" and "hardcut" are Speeduino's, read off a running
        // 202501: they choose how the limiter cuts, which is the limiter.
        DangerousRole.RevLimiter =>
            ["revlimiter", "revlimit", "revlim", "softlimit", "softlim", "hardlimit", "hardlim",
             "rpmlimit", "speedlimiter", "maxrpm", "limiterrpm", "rpmhardlimit", "rpmsoftlimit",
             "hardrevlimit", "softrevlimit", "kindoflimiting", "hardcut"],

        // Bare "launch" and "lnch" on purpose. Speeduino calls the switch
        // launchEnable and it was slipping through a list that only knew
        // launchRpm — on a board whose launch control is switched on with a
        // 2,700 rpm soft limit, so its limiter is not inert. A pin assignment
        // caught alongside it costs one extra confirmation; the switch itself
        // missed costs rather more.
        DangerousRole.LaunchControlRpm =>
            ["launch", "lnch", "flatshiftrpm", "flatshift", "antilagrpm", "antilag", "clutchrpm",
             "twostep", "2steprpm", "stagedlaunch"],

        DangerousRole.BoostLimit =>
            ["boostlimit", "boostctrlmax", "overboost", "maxboost", "boostcut", "boostfailsafe",
             "wastegatemax", "boostlimitkpa", "boostcontrolmax", "boostbygear"],

        DangerousRole.IgnitionCut =>
            ["ignitioncut", "igncut", "sparkcut", "coilcut", "shiftcuttime", "flatshiftign",
             "cuttimeign", "sparkcuttime"],

        // "afrprotect" guards the engine against running lean under load; the
        // time it cuts for is part of that guard, not a tuning preference.
        DangerousRole.FuelCut =>
            ["fuelcutrpm", "fuelcut", "overruncut", "decelfuelcutoff", "dfcorpm", "injectorcut",
             "fuelcutoff", "fuelcutoffrpm", "afrprotect"],

        _ => [],
    };
}
