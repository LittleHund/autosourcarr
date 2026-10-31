using AutoSourcarr.Core.Sources.Runtime;

namespace AutoSourcarr.Tests.Sources;

/// <summary>Phase 4: the Source browser identity keeps the 0.4.x presets and rules (source_browser.py).</summary>
public static class SourceBrowserPreferencesTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("source browser: presets, custom value rules, header-injection refusal and file round trip", () =>
        {
            Check.Equal(("default", ""), SourceBrowserPreferences.Validate("default", "ignored"));
            Check.Equal(SourceEngineEnvironment.DefaultUserAgent, SourceBrowserPreferences.Validate("chrome-windows", "").UserAgent);
            Check.True(SourceBrowserPreferences.Validate("chrome-linux", "").UserAgent.Contains("X11; Linux x86_64", StringComparison.Ordinal));
            Check.Equal(("custom", "My Agent/1.0"), SourceBrowserPreferences.Validate("custom", "  My Agent/1.0 "));
            foreach (var (preset, value, message) in new[]
            {
                ("nope", "x", "known user-agent preset"),
                ("custom", "   ", "Enter a custom user agent"),
                ("custom", "a\r\nX-Evil: 1", "line breaks"),
                ("browser", "tab\there", "line breaks"),
                ("custom", "é", "printable ASCII"),
                ("custom", new string('a', 1025), "printable ASCII"),
            })
            {
                try { SourceBrowserPreferences.Validate(preset, value); throw new InvalidOperationException("accepted " + preset); }
                catch (ArgumentException error) { Check.True(error.Message.Contains(message, StringComparison.Ordinal), error.Message); }
            }
            Check.Equal(6, SourceBrowserPreferences.PresetsJson().Count);
            using var temp = new Sandbox();
            Check.Equal(("default", ""), SourceBrowserPreferences.Read(temp.Path));
            SourceBrowserPreferences.Save(temp.Path, "custom", "My Agent/1.0");
            Check.Equal(("custom", "My Agent/1.0"), SourceBrowserPreferences.Read(temp.Path));
            File.WriteAllText(Path.Combine(temp.Path, SourceBrowserPreferences.FileName), "{\"preset\":\"custom\",\"user_agent\":\"bad\\nvalue\"}");
            Check.Equal(("default", ""), SourceBrowserPreferences.Read(temp.Path));
            Check.Equal(1, Directory.GetFiles(temp.Path).Length);
        });
    }
}
