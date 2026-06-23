using System;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Persisted product-analytics consent. Stores whether the user opted in, whether they've been
/// asked yet (so the first-run prompt shows only once), and a random per-install id used as the
/// PostHog distinct id. The id contains no personal data and is created only once the user has
/// opted in - an opted-out install never gets one.
/// </summary>
internal static class AnalyticsConsent
{
    private const string EnabledKey  = "analytics.enabled";
    private const string DecidedKey  = "analytics.decided";
    private const string DistinctKey = "analytics.distinctId";

    /// <summary>Whether usage events may be sent. Setting it also records that the user has decided.</summary>
    public static bool Enabled
    {
        get => AppSettings.Get(EnabledKey) == "1";
        set => AppSettings.SetMany((EnabledKey, value ? "1" : "0"), (DecidedKey, "1"));
    }

    /// <summary>True once the user has answered the first-run opt-in prompt.</summary>
    public static bool Decided => AppSettings.Get(DecidedKey) == "1";

    /// <summary>A stable random id for this install (no personal data), created on first use.</summary>
    public static string DistinctId
    {
        get
        {
            string? id = AppSettings.Get(DistinctKey);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                AppSettings.Set(DistinctKey, id);
            }
            return id;
        }
    }
}
