using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayStreamDiscriminator
{
    private enum Stage { Root, Key, Name, Colon, Value, Scalar, String, Nested, AfterValue, Complete, Failed }
    private Stage _stage;
    private bool _escape;
    private bool _nestedString;
    private bool _streamName;
    private int _depth;
    private readonly byte[] _name = new byte[64];
    private int _nameLength;
    private int _literalIndex;
    private bool _literalTrue;
    private GatewayModelClassification? _decisive;

    internal void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (_decisive is not null || _stage == Stage.Failed)
            {
                return;
            }
            ProcessByte(value);
        }
    }

    private void ProcessByte(byte value)
    {
        bool whitespace = value is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t';
        switch (_stage)
        {
                case Stage.Root:
                    if (!whitespace)
                    {
                        _stage = value == '{' ? Stage.Key : Stage.Failed;
                    }
                    break;
                case Stage.Key:
                    if (whitespace) { break; }
                    if (value == '}') { _stage = Stage.Complete; break; }
                    if (value != '"') { _stage = Stage.Failed; break; }
                    _nameLength = 1;
                    _name[0] = value;
                    _escape = false;
                    _stage = Stage.Name;
                    break;
                case Stage.Name:
                    ReadName(value);
                    break;
                case Stage.Colon:
                    if (!whitespace) { _stage = value == ':' ? Stage.Value : Stage.Failed; }
                    break;
                case Stage.Value:
                    ReadValue(value, whitespace);
                    break;
                case Stage.Scalar:
                    ReadScalar(value, whitespace);
                    break;
                case Stage.String:
                    if (!_escape && value == '"') { _stage = Stage.AfterValue; }
                    else { _escape = !_escape && value == '\\'; }
                    break;
                case Stage.Nested:
                    ReadNested(value);
                    break;
                case Stage.AfterValue:
                    if (!whitespace) { _stage = value == ',' ? Stage.Key : value == '}' ? Stage.Complete : Stage.Failed; }
                    break;
                case Stage.Complete:
                    if (!whitespace) { _stage = Stage.Failed; }
                    break;
                default: break;
        }
    }

    private void ReadName(byte value)
    {
        if (_nameLength < _name.Length) { _name[_nameLength] = value; }
        _nameLength++;
        if (!_escape && value == '"')
        {
            _streamName = IsStreamName();
            _stage = Stage.Colon;
        }
        else { _escape = !_escape && value == '\\'; }
    }

    private void ReadValue(byte value, bool whitespace)
    {
        if (whitespace) { return; }
        if (_streamName)
        {
            if (value is not ((byte)'t' or (byte)'f')) { _stage = Stage.Failed; return; }
            _literalTrue = value == 't';
            _literalIndex = 1;
            _stage = Stage.Scalar;
        }
        else if (value == '"') { _escape = false; _stage = Stage.String; }
        else if (value is (byte)'{' or (byte)'[')
        { _depth = 1; _nestedString = false; _stage = Stage.Nested; }
        else if (value is (byte)',' or (byte)'}' or (byte)'/' or (byte)':') { _stage = Stage.Failed; }
        else { _stage = Stage.Scalar; }
    }

    private void ReadScalar(byte value, bool whitespace)
    {
        if (_streamName)
        {
            ReadOnlySpan<byte> literal = _literalTrue ? "true"u8 : "false"u8;
            if (_literalIndex < literal.Length)
            {
                if (value != literal[_literalIndex++]) { _stage = Stage.Failed; }
            }
            else if (whitespace || value is (byte)',' or (byte)'}')
            { _decisive = _literalTrue ? GatewayModelClassification.DeclaredTrue : GatewayModelClassification.DeclaredFalse; }
            else { _stage = Stage.Failed; }
        }
        else if (whitespace) { _stage = Stage.AfterValue; }
        else if (value is (byte)',' or (byte)'}') { _stage = value == ',' ? Stage.Key : Stage.Complete; }
    }

    private void ReadNested(byte value)
    {
        if (_nestedString)
        {
            if (!_escape && value == '"') { _nestedString = false; }
            _escape = !_escape && value == '\\';
        }
        else if (value == '"') { _nestedString = true; _escape = false; }
        else if (value is (byte)'{' or (byte)'[')
        { if (++_depth >= 64) { _stage = Stage.Failed; } }
        else if (value is (byte)'}' or (byte)']')
        { if (--_depth == 0) { _stage = Stage.AfterValue; } }
    }

    internal GatewayModelClassification Finish() => _decisive
        ?? (_stage == Stage.Complete ? GatewayModelClassification.Absent : GatewayModelClassification.Quarantine);

    private bool IsStreamName()
    {
        if (_nameLength > _name.Length) { return false; }
        try
        {
            Utf8JsonReader reader = new(_name.AsSpan(0, _nameLength), isFinalBlock: true, state: default);
            return reader.Read() && reader.ValueTextEquals("stream"u8);
        }
        catch (JsonException) { return false; }
    }
}
