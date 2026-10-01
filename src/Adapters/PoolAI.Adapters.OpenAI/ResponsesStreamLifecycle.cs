using System.Text;
using System.Text.Json;

namespace PoolAI.Adapters.OpenAI;

// Mirrors the canonical fixture lifecycle, while retaining only bounded
// per-response state. No provider I/O or downstream writes belong here.
internal sealed class ResponsesStreamLifecycle
{
    private const int MaximumItems = 4096;
    private readonly SortedDictionary<int, Item> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private int _characters;
    private int _partCount;
    private bool _inProgress;

    internal void Accept(string eventName, JsonElement value)
    {
        if (eventName is "error" or "response.created") { return; }
        if (eventName is "response.in_progress") { _inProgress = true; return; }
        Require(_inProgress);
        if (eventName is "response.completed") { Complete(value.GetProperty("response")); return; }
        int index = value.GetProperty("output_index").GetInt32();
        if (eventName is "response.output_item.added") { AddItem(index, value.GetProperty("item")); return; }
        Require(_items.TryGetValue(index, out Item? item) && !item.Done);
        if (eventName is "response.output_item.done")
        {
            ValidateCompletedItem(item!, value.GetProperty("item"));
            item!.Done = true;
            return;
        }
        Require(string.Equals(value.GetProperty("item_id").GetString(), item!.Id, StringComparison.Ordinal));
        if (eventName.StartsWith("response.function_call_arguments.", StringComparison.Ordinal))
        {
            AcceptFunction(item, eventName, value);
            return;
        }
        Require(item.Type is "message");
        int partIndex = value.GetProperty("content_index").GetInt32();
        if (eventName is "response.content_part.added")
        {
            Require(_partCount < MaximumItems && !item.Parts.ContainsKey(partIndex));
            string text = value.GetProperty("part").GetProperty("text").GetString()!;
            Retain(text.Length);
            item.Parts.Add(partIndex, new Part(text));
            _partCount++;
            return;
        }
        Require(item.Parts.TryGetValue(partIndex, out Part? part) && !part.ContentDone);
        AcceptText(part!, eventName, value);
    }

    private void AddItem(int index, JsonElement value)
    {
        string id = value.GetProperty("id").GetString()!;
        Require(_items.Count < MaximumItems && !_items.ContainsKey(index) && _ids.Add(id));
        Retain(id.Length);
        Item item = new(id, value.GetProperty("type").GetString()!);
        if (item.Type is "function_call")
        {
            Require(value.GetProperty("arguments").GetString() is "");
            item.Name = value.GetProperty("name").GetString()!;
            item.CallId = value.GetProperty("call_id").GetString()!;
            Retain(item.Name.Length + item.CallId.Length);
        }
        _items.Add(index, item);
    }

    private void AcceptText(Part part, string eventName, JsonElement value)
    {
        switch (eventName)
        {
            case "response.output_text.delta":
                Require(!part.TextDone);
                Append(part.Text, value.GetProperty("delta").GetString()!);
                break;
            case "response.output_text.done":
                Require(!part.TextDone && string.Equals(part.Text.ToString(), value.GetProperty("text").GetString(), StringComparison.Ordinal));
                part.TextDone = true;
                break;
            case "response.content_part.done":
                Require(part.TextDone && string.Equals(part.Text.ToString(), value.GetProperty("part").GetProperty("text").GetString(), StringComparison.Ordinal));
                part.ContentDone = true;
                break;
            default:
                throw new JsonException("The response text lifecycle is unsupported.");
        }
    }

    private void AcceptFunction(Item item, string eventName, JsonElement value)
    {
        Require(item.Type is "function_call" && !item.ArgumentsDone);
        if (eventName is "response.function_call_arguments.delta")
        {
            Append(item.Arguments, value.GetProperty("delta").GetString()!);
            return;
        }
        string arguments = value.GetProperty("arguments").GetString()!;
        Require(string.Equals(item.Name, value.GetProperty("name").GetString(), StringComparison.Ordinal)
            && string.Equals(item.Arguments.ToString(), arguments, StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(arguments, new JsonDocumentOptions { MaxDepth = 64 });
        Require(document.RootElement.ValueKind == JsonValueKind.Object);
        item.ArgumentsDone = true;
    }

    private void Complete(JsonElement response)
    {
        JsonElement output = response.GetProperty("output");
        Require(output.GetArrayLength() == _items.Count);
        int index = 0;
        foreach ((int itemIndex, Item item) in _items)
        {
            Require(itemIndex == index && item.Done);
            ValidateCompletedItem(item, output[index++]);
        }
    }

    private static void ValidateCompletedItem(Item item, JsonElement value)
    {
        Require(string.Equals(item.Id, value.GetProperty("id").GetString(), StringComparison.Ordinal)
            && string.Equals(item.Type, value.GetProperty("type").GetString(), StringComparison.Ordinal));
        if (item.Type is "function_call")
        {
            Require(item.ArgumentsDone && string.Equals(item.Name, value.GetProperty("name").GetString(), StringComparison.Ordinal)
                && string.Equals(item.CallId, value.GetProperty("call_id").GetString(), StringComparison.Ordinal)
                && string.Equals(item.Arguments.ToString(), value.GetProperty("arguments").GetString(), StringComparison.Ordinal));
            return;
        }
        JsonElement content = value.GetProperty("content");
        Require(content.GetArrayLength() == item.Parts.Count);
        int index = 0;
        foreach ((int partIndex, Part part) in item.Parts)
        {
            Require(partIndex == index && part.ContentDone
                && string.Equals(part.Text.ToString(), content[index].GetProperty("text").GetString(), StringComparison.Ordinal));
            index++;
        }
    }

    private void Append(StringBuilder builder, string delta)
    {
        Retain(delta.Length);
        builder.Append(delta);
    }

    private void Retain(int count)
    {
        Require(count <= ResponsesResponseParser.MaximumJsonBytes / sizeof(char) - _characters);
        _characters += count;
    }

    private static void Require(bool condition)
    {
        if (!condition) { throw new JsonException("The response output lifecycle is inconsistent or exceeds its safety bound."); }
    }

    private sealed class Item(string id, string type)
    {
        internal string Id { get; } = id;
        internal string Type { get; } = type;
        internal string? Name { get; set; }
        internal string? CallId { get; set; }
        internal bool Done { get; set; }
        internal bool ArgumentsDone { get; set; }
        internal StringBuilder Arguments { get; } = new();
        internal SortedDictionary<int, Part> Parts { get; } = [];
    }

    private sealed class Part(string text)
    {
        internal StringBuilder Text { get; } = new(text);
        internal bool TextDone { get; set; }
        internal bool ContentDone { get; set; }
    }
}
