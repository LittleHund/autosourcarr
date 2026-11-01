using System.Globalization;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Reader.Extension;

/// <summary>
/// READ-5: what a phone needs to read from this server. Mihon (and forks) add an extension repository from a link
/// (mihon://extension-store?url=..., legacy tachiyomi://add-repo?url=...; checked against Mihon's AndroidManifest and
/// MainActivity). There is no link that fills a source's settings, so each server slot gets its address and key as
/// text to paste into "AutoSourcarr (n)" settings (the System page shows both as QR codes).
/// </summary>
public static class ReaderSetup
{
    /// <summary>The setup data for this server: repository URL and links, the reader address, and one row per slot.</summary>
    /// <param name="publicUrl">AUTOSOURCARR_PUBLIC_URL: the address phones reach this server at.</param>
    /// <param name="status">The served build (<see cref="ReaderExtensionRepository.Status"/>).</param>
    public static JsonObject Build(string publicUrl, JsonObject status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var address = Address(publicUrl);
        var warnings = new JsonArray();
        if (address.Length == 0) warnings.Add("Set AUTOSOURCARR_PUBLIC_URL to the address phones use to reach this server.");
        else if (!address.StartsWith("https://", StringComparison.Ordinal)) warnings.Add("The public address is not HTTPS; Android blocks plain HTTP unless the reader allows it. Use your reverse proxy's HTTPS address.");
        if (status["available"] is JsonValue available && available.TryGetValue<bool>(out var present) && !present)
            warnings.Add("No reader extension build is installed on this server; the repository link answers 404 until one is.");
        else if (status["signed_for_release"] is JsonValue signed && signed.TryGetValue<bool>(out var release) && !release)
            warnings.Add("This reader extension build is signed with a throwaway key (UNSIGNED-FOR-RELEASE); readers cannot update it to a release build.");
        var repository = address.Length == 0 ? "" : address + ReaderExtensionRepository.RoutePrefix + "/index.min.json";
        var escaped = Uri.EscapeDataString(repository);
        var count = status["slots"] is JsonValue slotValue && slotValue.TryGetValue<int>(out var slotCount) && slotCount > 0 ? slotCount : ReaderExtensionRepository.DefaultSlots;
        var known = (status["sources"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var slots = new JsonArray();
        for (var slot = 1; slot <= count; slot++)
        {
            var source = known.FirstOrDefault(row => row["slot"] is JsonValue value && value.TryGetValue<int>(out var number) && number == slot);
            slots.Add(new JsonObject
            {
                ["slot"] = slot,
                ["source_name"] = source?["name"]?.GetValue<string>() ?? SlotName(slot),
                ["source_id"] = source?["source_id"]?.GetValue<string>() ?? "",
                ["address"] = address,
                ["settings_text"] = SettingsText(slot, address)
            });
        }
        return new()
        {
            ["address"] = address,
            ["repository_url"] = repository,
            ["mihon_link"] = repository.Length == 0 ? "" : "mihon://extension-store?url=" + escaped,
            ["tachiyomi_link"] = repository.Length == 0 ? "" : "tachiyomi://add-repo?url=" + escaped,
            ["key_header"] = "X-AutoSourcarr-API-Key",
            ["reader_api"] = "/api/reader/v1",
            ["slots"] = slots,
            ["extension"] = status.DeepClone(),
            ["warnings"] = warnings
        };
    }

    /// <summary>The source name of a slot, as build.gradle.kts declares it.</summary>
    public static string SlotName(int slot) => slot == 1 ? "AutoSourcarr" : "AutoSourcarr (" + slot.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>The text a phone copies into the slot's settings; the key is filled in by the page (never stored here).</summary>
    public static string SettingsText(int slot, string address) =>
        SlotName(slot) + " settings\nServer " + slot.ToString(CultureInfo.InvariantCulture) + " address: " + address + "\nServer "
        + slot.ToString(CultureInfo.InvariantCulture) + " reader key: ";

    /// <summary>An absolute http(s) address without a trailing slash, query or fragment; empty when it is not one.</summary>
    public static string Address(string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl) || !Uri.TryCreate(publicUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return "";
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
