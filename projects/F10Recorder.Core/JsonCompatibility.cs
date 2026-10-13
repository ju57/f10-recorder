using System.Text.Json;

namespace System.Web.Script.Serialization;

// Preserve the prototype's field-based JSON schema when moving off Framework.
// Unknown legacy members are accepted; Settings still validates every loaded value.
public sealed class JavaScriptSerializer
{
    static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
    public string Serialize(object value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);
    public T Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
}
