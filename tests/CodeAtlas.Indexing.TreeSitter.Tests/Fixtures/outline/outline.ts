// 调用大纲夹具：中文注释在前，验证 Pos 是 UTF-16 字符偏移 😀
import { api } from './api';

export function handle(items: number[], a?: Svc) {
  start();
  for (let i = forInit(); forCond(i); forStep()) { forBody(); }
  for (const x of forOfColl()) forOfBody();
  for (const k in forInObj()) forInBody();
  while (whileCond()) { whileBody(); }
  do { doBody(); } while (doCond());
  if (ifCond()) ifThen(); else if (elifCond()) elifThen(); else elseBody();
  switch (switchValue()) { case 1: caseBody(); break; default: defaultBody(); }
  const t = ternCond() ? ternA() : ternB();
  const n = coalLeft() ?? coalRight();
  a?.safeFirst().safeThen();
  try { tryBody(); } catch (e) { catchBody(); } finally { finallyBody(); }
  run(() => arrowBody());
  run(function () { fnExprBody(); });
  for (const item of items) {
    if (item > 0) { nestedIfInLoop(); }
    nestedLoopBody();
    items.forEach(v => arrowInLoop(v));
  }
  api.chain().mid().
    tail();
  end();
}

export const arrowFn = async () => {
  await arrowFnTop();
  if (arrowFnCond()) arrowFnThen();
};

class Svc {
  field = () => fieldArrowTop();
  method() {
    this.methodTop();
    [1, 2].map(x => this.methodCallback(x));
  }
}
