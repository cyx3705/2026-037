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
            var current = Load();
            if (!current.TryGetValue(Key(item.Bridge, item.ConversationId), out var marker))
                return item.Unread;

            // 有桥端未读数时，保留桥的精确计数；没有未读能力的桥用本地时间水位
            // 兜底，只能可靠地表达「有新消息」，所以显示为 1。
            if (item.Unread <= 0)
                return item.LastTsMs > marker.Through ? 1 : 0;

            if (item.LastTsMs <= marker.Through && item.Unread <= marker.Unread) return 0;
            return item.Unread > marker.Unread ? item.Unread - marker.Unread : item.Unread;
        }
    }

    /// <summary>第一次看到不提供未读数的会话时建立基线，避免把整段历史都报成新消息。</summary>
    public void Observe(IEnumerable<FamaConversation> items)
    {
        lock (_gate)
        {
            var current = Load();
            Dictionary<string, Marker>? next = null;
            foreach (var item in items)
            {
                if (item.Unread > 0 || item.LastTsMs <= 0) continue;
                var key = Key(item.Bridge, item.ConversationId);
                if (current.ContainsKey(key)) continue;
                next ??= new Dictionary<string, Marker>(current);
                next[key] = new Marker(item.LastTsMs, 0);
            }

            if (next != null)
                Save(next);
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
            Save(next);
            return true;
        }
    }

    private void Save(Dictionary<string, Marker> next)
    {
        if (path != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, path, true);
        }
        _markers = next;
    }
}
