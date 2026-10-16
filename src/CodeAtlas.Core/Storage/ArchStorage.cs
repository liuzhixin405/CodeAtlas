using System.Text.Json;
using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Storage;

/// <summary>架构事实的整表替换写入与读取。模型小（几十个节点），不做增量。</summary>
public static class ArchStorage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Replace(IndexDatabase db, ArchModel model)
    {
        using var batch = db.BeginBatch();
        ArchFingerprintStorage.Set(db, ArchFingerprintStorage.Post, null); // 结果换了，指纹作废（后处理写完再记新的）
        db.Execute("DELETE FROM arch_nodes");
        db.Execute("DELETE FROM arch_edges");
        db.Execute("DELETE FROM arch_outbound");
        db.Execute("DELETE FROM arch_conflicts");
        foreach (var n in model.Nodes)
            db.Execute("""
                INSERT INTO arch_nodes (key, kind, subkind, name, path, source, members_json, ports_json, evidence_json)
                VALUES ($k, $kind, $sub, $n, $p, $s, $m, $ports, $e)
                """,
                ("$k", n.Key), ("$kind", n.Kind.ToString()), ("$sub", n.SubKind), ("$n", n.Name), ("$p", n.Path), ("$s", n.Source.ToString()),
                ("$m", JsonSerializer.Serialize(n.Members, Json)), ("$ports", JsonSerializer.Serialize(n.Ports, Json)), ("$e", JsonSerializer.Serialize(n.Evidence, Json)));
        foreach (var e in model.Edges)
            db.Execute("""
                INSERT INTO arch_edges (from_key, to_key, protocol, confidence, source, config_key, url, evidence_json)
                VALUES ($f, $t, $p, $c, $s, $ck, $u, $e)
                """,
                ("$f", e.FromKey), ("$t", e.ToKey), ("$p", e.Protocol), ("$c", e.Confidence), ("$s", e.Source.ToString()),
                ("$ck", e.ConfigKey), ("$u", e.Url), ("$e", JsonSerializer.Serialize(e.Evidence, Json)));
        foreach (var o in model.Outbound)
            db.Execute("""
                INSERT INTO arch_outbound (container_key, rule_id, protocol, client_name, config_key, url, file, line, target_key)
                VALUES ($c, $r, $p, $cn, $ck, $u, $f, $l, $t)
                """,
                ("$c", o.ContainerKey), ("$r", o.RuleId), ("$p", o.Protocol), ("$cn", o.ClientName), ("$ck", o.ConfigKey), ("$u", o.Url),
                ("$f", o.File), ("$l", o.Line), ("$t", o.TargetKey));
        foreach (var c in model.Conflicts)
            db.Execute("INSERT INTO arch_conflicts (kind, message) VALUES ($k, $m)", ("$k", c.Kind), ("$m", c.Message));
        batch.Commit();
    }

    public static ArchModel Read(IndexDatabase db)
    {
        var nodes = new List<ArchNode>();
        using (var cmd = db.CreateCommand("SELECT key, kind, subkind, name, path, source, members_json, ports_json, evidence_json FROM arch_nodes ORDER BY kind, key"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                nodes.Add(new ArchNode(r.GetString(0), Enum.Parse<ArchNodeKind>(r.GetString(1)), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    Enum.Parse<ArchSource>(r.GetString(5)),
                    JsonSerializer.Deserialize<List<Evidence>>(r.GetString(8), Json) ?? [],
                    JsonSerializer.Deserialize<List<string>>(r.GetString(6), Json) ?? [],
                    JsonSerializer.Deserialize<List<int>>(r.GetString(7), Json) ?? []));

        var edges = new List<ArchEdge>();
        using (var cmd = db.CreateCommand("SELECT from_key, to_key, protocol, confidence, source, config_key, url, evidence_json FROM arch_edges ORDER BY from_key, to_key, protocol"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                edges.Add(new ArchEdge(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), Enum.Parse<ArchSource>(r.GetString(4)),
                    JsonSerializer.Deserialize<List<Evidence>>(r.GetString(7), Json) ?? [],
                    r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));

        var outbound = new List<Outbound>();
        using (var cmd = db.CreateCommand("SELECT container_key, rule_id, protocol, client_name, config_key, url, file, line, target_key FROM arch_outbound ORDER BY container_key, file, line"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                outbound.Add(new Outbound(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8)));

        var conflicts = new List<ArchConflict>();
        using (var cmd = db.CreateCommand("SELECT kind, message FROM arch_conflicts ORDER BY id"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) conflicts.Add(new ArchConflict(r.GetString(0), r.GetString(1)));

        return new ArchModel(nodes, edges, outbound, conflicts);
    }
}
