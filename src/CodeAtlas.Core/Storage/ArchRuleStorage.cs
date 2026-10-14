using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Storage;

/// <summary>声明规则（arch_rules）的整表替换与读取。读出的顺序 = 写入顺序（按 id），DSM 用下标当规则 id。</summary>
public static class ArchRuleStorage
{
    public static void Replace(IndexDatabase db, IReadOnlyList<ArchRule> rules)
    {
        using var batch = db.BeginBatch();
        db.Execute("DELETE FROM arch_rules");
        foreach (var r in rules)
            db.Execute("""
                INSERT INTO arch_rules (kind, from_pattern, from_match, to_pattern, to_match, file, line, test_name, source)
                VALUES ($k, $fp, $fm, $tp, $tm, $f, $l, $t, $s)
                """, ("$k", r.Kind), ("$fp", r.FromPattern), ("$fm", r.FromMatch), ("$tp", r.ToPattern), ("$tm", r.ToMatch),
                ("$f", r.File), ("$l", r.Line), ("$t", r.TestName), ("$s", r.Source));
        batch.Commit();
    }

    public static List<ArchRule> Read(IndexDatabase db)
    {
        var rules = new List<ArchRule>();
        using var cmd = db.CreateCommand("SELECT kind, from_pattern, from_match, to_pattern, to_match, file, line, test_name, source FROM arch_rules ORDER BY id");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rules.Add(new ArchRule(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8)));
        return rules;
    }

    /// <summary>同 Read；与任务说明里的名字保持一致。</summary>
    public static List<ArchRule> Load(IndexDatabase db) => Read(db);
}
