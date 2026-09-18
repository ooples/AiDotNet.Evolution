using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AiDotNet.Evolution.Programs;

internal static class JudgeJson
{
    internal static bool TryExtract(string text, int maxChars, out JObject json)
    {
        json = null!;
        if (text.Length > maxChars) return false;
        int first = text.IndexOf('{'), last = text.LastIndexOf('}');
        if (first < 0 || last < first) return false;
        try
        {
            using var reader = new JsonTextReader(new StringReader(text.Substring(first, last - first + 1)))
            { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
            json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) return false;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryReadNumber(JObject json, string name, out double value)
    {
        value = 0;
        var token = json[name];
        if (token?.Type is not (JTokenType.Integer or JTokenType.Float)) return false;
        try { value = token.Value<double>(); return double.IsFinite(value); }
        catch (Exception e) when (e is OverflowException or FormatException or InvalidCastException) { return false; }
    }
}
