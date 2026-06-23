using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace ExtrabbitCode.Inventor.MetaReader.App;

/// <summary>
/// Minimal, privacy-respecting product analytics via PostHog (EU). Sends anonymous feature-usage
/// events only - never file names, paths, property values or any document content. Events are sent
/// only when the user has opted in (<see cref="AnalyticsConsent"/>); never in the docs snapshotter
/// or ephemeral mode. Capture is best-effort and fire-and-forget, so analytics can never block the
/// UI or surface an error to the user.
/// </summary>
internal static class Analytics
{
    // Project API key (write-only ingestion key, safe to ship in the client) and the EU capture host.
    private const string ApiKey = "phc_m28WKHw4KSxype8jZERmP7UUH8Lsnpj6UScGg9ArpyZN";
    private const string CaptureUrl = "https://eu.i.posthog.com/capture/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static string _appVersion = "?";
    private static string _os = "";

    /// <summary>Cache the non-identifying environment context. Call once at startup.</summary>
    public static void Init()
    {
        _appVersion = AppInfo.Version;
        _os = Environment.OSVersion.VersionString;
    }

    /// <summary>True when events will actually be sent (opted in, not docs/ephemeral mode).</summary>
    private static bool Enabled =>
        !AppSettings.Ephemeral && !App.ShootMode && AnalyticsConsent.Enabled;

    /// <summary>Fire-and-forget capture of a feature-usage event. Properties must contain no
    /// personal or document-identifying data (no file names, paths, or property values).</summary>
    public static void Capture(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (!Enabled) { return; }
        _ = SendAsync(eventName, AnalyticsConsent.DistinctId, properties);
    }

    private static async Task SendAsync(string eventName, string distinctId, IReadOnlyDictionary<string, object?>? properties)
    {
        try
        {
            Dictionary<string, object?> props = new()
            {
                ["app_version"] = _appVersion,
                ["os"] = _os,
            };
            if (properties != null)
            {
                foreach (KeyValuePair<string, object?> kv in properties) { props[kv.Key] = kv.Value; }
            }

            var payload = new
            {
                api_key = ApiKey,
                @event = eventName,
                distinct_id = distinctId,
                properties = props,
            };

            string json = JsonSerializer.Serialize(payload, JsonOpts);
            using StringContent content = new(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage _ = await Http.PostAsync(CaptureUrl, content).ConfigureAwait(false);
            // Response is intentionally ignored; PostHog returns 200 {"status":1} on success.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Analytics capture for {Event} failed", eventName);
        }
    }
}
