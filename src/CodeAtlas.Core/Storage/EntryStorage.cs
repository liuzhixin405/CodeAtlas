using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Storage;

/// <summary>入口与路由匹配的整表替换与读取。</summary>
public static class EntryStorage
{
    public static void Replace(IndexDatabase db, IReadOnlyList<EntryPoint> entries, IReadOnlyList<RouteMatch> matches)
    {
        using var batch = db.BeginBatch();
        db.Execute("DELETE FROM entry_points");
        db.Execute("DELETE FROM route_matches");
        foreach (var e in entries)
            db.Execute("""
                INSERT INTO entry_points (symbol_id, container_key, rule_id, kind, route, http_method, file, line)
                VALUES ($s, $c, $r, $k, $route, $m, $f, $l)
                """, ("$s", e.SymbolId), ("$c", e.ContainerKey), ("$r", e.RuleId), ("$k", e.Kind), ("$route", e.Route), ("$m", e.HttpMethod), ("$f", e.File), ("$l", e.Line));
        foreach (var m in matches)
            db.Execute("""
                INSERT INTO route_matches (from_container, file, line, path, entry_symbol_id, to_container)
                VALUES ($fc, $f, $l, $p, $e, $tc)
                """, ("$fc", m.FromContainerKey), ("$f", m.File), ("$l", m.Line), ("$p", m.Path), ("$e", m.EntrySymbolId), ("$tc", m.ToContainerKey));
        batch.Commit();
    }

    public static (List<EntryPoint> Entries, List<RouteMatch> Matches) Read(IndexDatabase db)
    {
        var entries = new List<EntryPoint>();
        using (var cmd = db.CreateCommand("SELECT symbol_id, container_key, rule_id, kind, route, http_method, file, line FROM entry_points ORDER BY container_key, kind, route, file, line"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                entries.Add(new EntryPoint(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetInt32(7)));
        var matches = new List<RouteMatch>();
        using (var cmd = db.CreateCommand("SELECT from_container, file, line, path, entry_symbol_id, to_container FROM route_matches ORDER BY from_container, file, line"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) matches.Add(new RouteMatch(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetString(5)));
        return (entries, matches);
    }
}
