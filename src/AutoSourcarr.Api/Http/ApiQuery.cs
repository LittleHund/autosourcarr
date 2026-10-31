using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Http;

public static class ApiQuery
{
    public static bool Boolean(HttpRequest request, string name, bool fallback)
    {
        if (!request.Query.TryGetValue(name, out var values)) return fallback;
        return values.LastOrDefault()?.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new ApiProblem(422, "Invalid " + name + " boolean")
        };
    }
    public static int Integer(HttpRequest request, string name, int fallback, int minimum, int maximum)
    {
        if (!request.Query.TryGetValue(name, out var values)) return fallback;
        var text = values.LastOrDefault();
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) || decimal.Truncate(value) != value || value < minimum || value > maximum)
            throw new ApiProblem(422, "Invalid " + name + " integer range");
        return (int)value;
    }
}
