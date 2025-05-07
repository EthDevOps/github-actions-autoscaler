using System.Text.Json;
using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator.Models;

public class EnvironmentAwareJsonConverter<T> : JsonConverter<T> where T : class
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string value = reader.GetString() ?? string.Empty;
            if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                string envVarName = value.Substring(4);
                return Environment.GetEnvironmentVariable(envVarName) as T;
            }
            return value as T;
        }

        return JsonSerializer.Deserialize<T>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}