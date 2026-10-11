using System.Text.Json;

namespace HistoryFama;

/// <summary>REQ-FAMA-002：只保存本地已读水位，不存消息，也不改变平台端的已读状态。</summary>
internal sealed class FamaReadState(string? path)
{
    private readonly object _gate = new();
    private Dictionary<string, Marker>? _markers;
    private sealed record Marker(long Through, int Unread);

    private Dictionary<string, Marker> Load()
        => _markers ??= path != null && File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, Marker>>(File.ReadAllText(path))
                ?? throw new IOException("本地已读记录为空或无效。")
            : [];

    private static string Key(string bridge, string conversation)
        => JsonSerializer.Serialize(new[] { bridge.ToUpperInvariant(), conversation });

    public int Unread(FamaConversation item)
    {
        lock (_gate)
        {
            if (item.Unread <= 0) return 0;
            if (!Load().TryGetValue(Key(item.Bridge, item.ConversationId), out var marker)) return item.Unread;
            if (item.LastTsMs <= marker.Through && item.Unread <= marker.Unread) return 0;
            return item.Unread > marker.Unread ? item.Unread - marker.Unread : item.Unread;
        }
    }

    public bool Mark(string bridge, string conversation, long through, int unread)
    {
        if (through <= 0 || unread < 0) return false;
        lock (_gate)
        {
            var current = Load();
            var key = Key(bridge, conversation);
            if (current.TryGetValue(key, out var old)
                && (old.Through > through || old.Through == through && old.Unread >= unread)) return false;
            var next = new Dictionary<string, Marker>(current) { [key] = new(through, unread) };
            if (path != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(next));
                File.Move(temporary, path, true);
            }
            _markers = next;
            return true;
        }
    }
}
