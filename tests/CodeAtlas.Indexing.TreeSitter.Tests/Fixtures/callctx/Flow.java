package flow;

import java.util.List;

// 调用大纲夹具：每个调用名唯一，测试逐条断言它所在的控制结构
class Flow {
    void run(List<Integer> list) {
        setup();
        for (int i = forInit(); forCond(i); forStep(i)) { inFor(); }
        for (var x : forEachSrc()) { inForEach(); }
        while (whileCond()) { inWhile(); }
        do { inDo(); } while (doCond());
        if (ifCond()) { inIf(); } else if (elseIfCond()) { inElseIf(); } else { inElse(); }
        switch (switchVal()) { case 1: inCase(); break; default: inDefault(); }
        int r = switch (switchExpr()) { case 1 -> inArrow(); default -> { yield inYield(); } };
        int t = ternCond() ? ternA() : ternB();
        try (var res = openRes()) { inTry(); } catch (RuntimeException ex) { inCatch(); } finally { inFinally(); }
        list.stream().map(x -> inLambda(x)).forEach(this::methodRef);
        new Thread(threadArg()) { public void run() { inAnon(); } };
        for (int y : list) {
            if (loopIfCond()) { inLoopIf(); }
            list.forEach(z -> inLoopLambda(z));
        }
        after();
        makeBuilder()
            .chainA()
            .chainB();
        boolean flag = andLeft() && andRight() || orRight();
        class Local { void localMethod() { inLocal(); } }
    }
}
