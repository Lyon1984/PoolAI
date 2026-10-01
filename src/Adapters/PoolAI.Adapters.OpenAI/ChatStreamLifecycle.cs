using System.Text.Json;

namespace PoolAI.Adapters.OpenAI;

/// <summary>Identity/order validation retains metadata, never streamed content or arguments.</summary>
internal sealed class ChatStreamLifecycle
{
    private const int MaximumIdentities = 4096;
    private const int MaximumRetainedCharacters = 16 * 1024 * 1024;
    private readonly Dictionary<int, Choice> _choices = [];
    private string? _id;
    private string? _model;
    private long _created;
    private bool _usage;
    private int _identities;
    private int _characters;

    internal bool Accept(JsonElement value)
    {
        string id = value.GetProperty("id").GetString()!;
        string model = value.GetProperty("model").GetString()!;
        long created = value.GetProperty("created").GetInt64();
        if (_id is null)
        {
            _id = id;
            _model = model;
            _created = created;
            Retain(id.Length + model.Length);
        }
        else if (!string.Equals(id, _id, StringComparison.Ordinal)
            || !string.Equals(model, _model, StringComparison.Ordinal) || created != _created)
        {
            throw new JsonException("The Chat stream identity changed.");
        }

        JsonElement choices = value.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array) { throw new JsonException(); }
        if (choices.GetArrayLength() == 0)
        {
            if (_usage || _choices.Count == 0 || _choices.Values.Any(static choice => !choice.Finished)
                || !value.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The Chat usage chunk is out of order.");
            }
            _usage = true;
            return true;
        }
        if (_usage || value.TryGetProperty("usage", out JsonElement counter) && counter.ValueKind != JsonValueKind.Null)
        {
            throw new JsonException("A content chunk cannot carry final usage.");
        }

        HashSet<int> indices = [];
        foreach (JsonElement item in choices.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
            int index = ChatResponseParser.Index(item);
            if (!indices.Add(index)) { throw new JsonException("A choice index is repeated."); }
            AcceptChoice(index, item);
        }
        return false;
    }

    private void AcceptChoice(int index, JsonElement item)
    {
            if (!_choices.TryGetValue(index, out Choice? choice))
            {
                AddIdentity();
                choice = new Choice();
                _choices.Add(index, choice);
            }
            if (choice.Finished) { throw new JsonException("A finished choice cannot continue."); }
            JsonElement delta = item.GetProperty("delta");
            if (!ChatProtocolAdapter.Only(delta, "role", "content", "tool_calls")
                || delta.TryGetProperty("role", out JsonElement role)
                    && (role.ValueKind != JsonValueKind.String || !string.Equals(role.GetString(), "assistant", StringComparison.Ordinal))
                || delta.TryGetProperty("content", out JsonElement content)
                    && content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new JsonException("The Chat delta is outside Release 1.");
            }
            if (delta.TryGetProperty("tool_calls", out JsonElement tools)) { AcceptTools(choice, tools); }
            JsonElement reason = item.GetProperty("finish_reason");
            if (reason.ValueKind != JsonValueKind.Null)
            {
                if (reason.ValueKind != JsonValueKind.String
                    || reason.GetString() is not ("stop" or "length" or "tool_calls" or "content_filter" or "function_call"))
                {
                    throw new JsonException("The Chat finish reason is invalid.");
                }
                if (choice.Tools.Values.Any(static tool => tool.Id is null || tool.Name is null || !tool.FunctionType))
                {
                    throw new JsonException("A streamed function identity is incomplete.");
                }
                choice.Finished = true;
            }
    }

    internal void Complete(bool requireUsage)
    {
        if (_choices.Count == 0 || _choices.Values.Any(static choice => !choice.Finished) || requireUsage && !_usage)
        {
            throw new JsonException("The Chat stream cannot finish before all choices and requested usage.");
        }
    }

    private void AcceptTools(Choice choice, JsonElement tools)
    {
        if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() == 0) { throw new JsonException(); }
        HashSet<int> indices = [];
        foreach (JsonElement item in tools.EnumerateArray())
        {
            if (!ChatProtocolAdapter.Only(item, "index", "id", "type", "function")) { throw new JsonException(); }
            int index = ChatResponseParser.Index(item);
            if (!indices.Add(index)) { throw new JsonException(); }
            if (!choice.Tools.TryGetValue(index, out Tool? tool))
            {
                AddIdentity();
                tool = new Tool();
                choice.Tools.Add(index, tool);
            }
            if (item.TryGetProperty("id", out JsonElement id))
            {
                if (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } text
                    || tool.Id is not null && !string.Equals(tool.Id, text, StringComparison.Ordinal)) { throw new JsonException(); }
                if (tool.Id is null)
                {
                    if (!choice.ToolIds.Add(text)) { throw new JsonException(); }
                    Retain(text.Length);
                    tool.Id = text;
                }
            }
            if (item.TryGetProperty("type", out JsonElement type))
            {
                if (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "function", StringComparison.Ordinal)) { throw new JsonException(); }
                tool.FunctionType = true;
            }
            if (item.TryGetProperty("function", out JsonElement function))
            {
                if (!ChatProtocolAdapter.Only(function, "name", "arguments") || !function.EnumerateObject().Any()) { throw new JsonException(); }
                if (function.TryGetProperty("name", out JsonElement name))
                {
                    if (name.ValueKind != JsonValueKind.String || name.GetString() is not string text
                        || !ChatProtocolAdapter.WithinLength(text, 1, 64)
                        || tool.Name is not null && !string.Equals(tool.Name, text, StringComparison.Ordinal)) { throw new JsonException(); }
                    if (tool.Name is null)
                    {
                        Retain(text.Length);
                        tool.Name = text;
                    }
                }
                if (function.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException();
                }
            }
        }
    }

    private void AddIdentity()
    {
        if (++_identities > MaximumIdentities) { throw new JsonException("The upstream identity bound was exceeded."); }
    }
    private void Retain(int characters)
    {
        _characters = checked(_characters + characters);
        if (_characters > MaximumRetainedCharacters) { throw new JsonException("The upstream metadata bound was exceeded."); }
    }
    private sealed class Choice
    {
        internal bool Finished { get; set; }
        internal Dictionary<int, Tool> Tools { get; } = [];
        internal HashSet<string> ToolIds { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Tool
    {
        internal string? Id { get; set; }
        internal string? Name { get; set; }
        internal bool FunctionType { get; set; }
    }
}
