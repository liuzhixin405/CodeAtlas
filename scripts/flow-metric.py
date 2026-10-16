"""
任意方法时序图的"业务调用覆盖率"（只读，走服务端 HTTP 接口；与 CLI 的 `codeatlas arch <repo> --flow-coverage <容器键>` 同一算法、同一批抽样）。

  python scripts/flow-metric.py --port 47800 --token T [--container 容器键] [--sample 30] [--levels 2] [--endpoint symbol|arch] [--json]

1. 容器：不给就取 HTTP 入口最多的那个；HTTP 入口按 (id * 2654435761) & 0xffffffff 排序（确定性的"随机"），依次取；
2. 根 = 入口调用大纲里第一个 dispatch 步骤（含挂在分发调用下的）的目标；没有分派就用入口本身；
3. 业务调用 = 根的 /api/symbols/{根}/outline 里 kind 为 call / dispatch 且 expandable 的步骤目标，
   加上各步 children 里 kind 为 impl / dispatch 且 expandable 的目标；一个都没有的入口跳过，直到凑够 sample 个；
4. 命中 = /api/symbols/{根}/flow 里深度 1..levels 的节点出现至少一个业务调用目标；平均召回 = 每个根看到的业务调用占比的平均；
5. 下一层：根的第一个业务调用目标再当一次根（Service / 仓储方法，多数不在入口表里，这才是"任意方法"），同样判定。

--endpoint arch 改用 /api/arch/flow?entry={根}（旧服务没有 /api/symbols/{id}/flow 时做对照：Handler 与入口本身都在入口表里）。
"""
import argparse
import json
import sys
import urllib.parse
import urllib.request


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--token", default="")
    ap.add_argument("--container")
    ap.add_argument("--sample", type=int, default=30)
    ap.add_argument("--levels", type=int, default=2)
    ap.add_argument("--endpoint", choices=["symbol", "arch"], default="symbol")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")  # Windows 控制台默认 GBK，中文会乱；与 codeatlas 的输出一致用 UTF-8
    base = f"http://127.0.0.1:{a.port}"

    def get(path):
        req = urllib.request.Request(base + path, headers={"X-CodeAtlas-Token": a.token})
        with urllib.request.urlopen(req, timeout=600) as r:
            return json.loads(r.read().decode("utf-8"))

    container = a.container
    if not container:
        keys = [n["key"] for n in get("/api/arch/containers")["nodes"] if n["kind"] == "container"]
        counts = {k: len(get("/api/arch/entries?kind=http&container=" + urllib.parse.quote(k))) for k in keys}
        container = max(counts, key=lambda k: (counts[k], k))
    entries = get("/api/arch/entries?kind=http&container=" + urllib.parse.quote(container))
    entries.sort(key=lambda e: ((e["symbolId"] * 2654435761) & 0xFFFFFFFF, e["symbolId"]))

    def outline(sid):
        return get(f"/api/symbols/{sid}/outline")

    def first_dispatch(o):
        for st in o["steps"]:
            if st["kind"] == "dispatch" and st["target"]:
                return st["target"]["id"]
            for c in st["children"]:
                if c["kind"] == "dispatch" and c["target"]:
                    return c["target"]["id"]
        return None

    def business(o):
        """业务调用目标，按大纲顺序去重。"""
        out = []
        for st in o["steps"]:
            if st["kind"] in ("call", "dispatch") and st["expandable"] and st["target"] and st["target"]["id"] not in out:
                out.append(st["target"]["id"])
            for c in st["children"]:
                if c["kind"] in ("impl", "dispatch") and c["expandable"] and c["target"] and c["target"]["id"] not in out:
                    out.append(c["target"]["id"])
        return out

    def seen_count(root, targets):
        flow = get(f"/api/symbols/{root}/flow" if a.endpoint == "symbol" else f"/api/arch/flow?entry={root}")
        seen = set()

        def walk(n):
            if 1 <= n["depth"] <= a.levels and n["symbolId"] is not None:
                seen.add(n["symbolId"])
            for c in n["children"]:
                walk(c)

        walk(flow["root"])
        return sum(1 for t in targets if t in seen)

    items, skipped = [], 0
    for e in entries:
        if len(items) >= a.sample:
            break
        eo = outline(e["symbolId"])
        root = first_dispatch(eo) or e["symbolId"]
        targets = business(eo if root == e["symbolId"] else outline(root))
        if not targets:
            skipped += 1
            continue
        seen = seen_count(root, targets)
        # 下一层：根的第一个业务调用目标再当一次根（多数不在入口表里；--endpoint arch 时旧服务给不了非入口的流程，跳过）
        next_root, next_hit = None, None
        if a.endpoint == "symbol":
            nt = business(outline(targets[0]))
            if nt:
                next_root, next_hit = targets[0], seen_count(targets[0], nt) > 0
        items.append({"entryId": e["symbolId"], "rootId": root, "targets": len(targets), "seen": seen, "hit": seen > 0,
                      "nextRootId": next_root, "nextHit": next_hit})

    hits = sum(1 for i in items if i["hit"])
    ratio = hits / len(items) if items else 0.0
    # 召回：每个根的业务调用里前 levels 层看到了几成，再取平均（比"至少一个"严，看排序把多少业务调用留在了第一屏）
    recall = sum(i["seen"] / i["targets"] for i in items) / len(items) if items else 0.0
    nxt = [i for i in items if i["nextHit"] is not None]
    next_hits = sum(1 for i in nxt if i["nextHit"])
    next_ratio = next_hits / len(nxt) if nxt else 0.0
    report = {"containerKey": container, "entries": len(entries), "sampled": len(items), "skipped": skipped, "hits": hits,
              "levels": a.levels, "ratio": ratio, "recall": recall, "nextSampled": len(nxt), "nextHits": next_hits, "nextRatio": next_ratio,
              "items": items}
    if a.json:
        print(json.dumps(report, ensure_ascii=False))
    else:
        print(f"HTTP 入口 {len(entries)}  抽样 {len(items)}（跳过没有业务调用的 {skipped}）  前 {a.levels} 层命中 {hits}  覆盖率 {ratio:.1%}  平均召回 {recall:.1%}")
        if a.endpoint == "symbol":
            print(f"下一层方法为根  {len(nxt)} 个  命中 {next_hits}  覆盖率 {next_ratio:.1%}")
        for i in items:
            if not i["hit"]:
                print(f"  未命中  入口 {i['entryId']}  根 {i['rootId']}  业务调用 {i['targets']} 个")
    return 0


if __name__ == "__main__":
    sys.exit(main())
