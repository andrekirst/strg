namespace Strg.Core.Domain;

/// <summary>
/// Usage ratios at which <see cref="Services.IQuotaService.CommitAsync"/> publishes a
/// <see cref="Events.QuotaWarningEvent"/>. Thresholds fire on *crossing* — a commit that takes
/// usage from 79% to 81% publishes once; a subsequent 82% → 83% commit does not. This prevents
/// a burst of small uploads inside the warning band from flooding the notification centre.
///
/// <para><b>Why two thresholds?</b> Warning (80%) is the "plan your cleanup" signal; Critical
/// (95%) is the "next upload will likely fail" signal. Collapsing them into one would force
/// the user to choose between early warning and urgent-action fatigue.</para>
///
/// <para>Notification payloads carry the level in <c>PayloadJson</c> (e.g.
/// <c>{"level":"warning","usedBytes":...}</c>). The client decides how to render each.</para>
/// </summary>
public static class QuotaThresholds
{
    public const double Warning = 0.80;
    public const double Critical = 0.95;

    public const string WarningLevel = "warning";
    public const string CriticalLevel = "critical";

    public const string NotificationType = "quota.warning";
}
