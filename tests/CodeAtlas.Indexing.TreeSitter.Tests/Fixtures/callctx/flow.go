package flow

// 调用大纲夹具：每个调用名唯一，测试逐条断言它所在的控制结构
func Run(items []int, ch chan int, out chan int) {
	setup()
	for i := forInit(); forCond(i); forStep(i) {
		inFor(i)
	}
	for _, it := range rangeSrc() {
		inRange(it)
	}
	for whileCond() {
		inWhile()
	}
	for {
		inForever()
		break
	}
	if ok := ifInit(); ifCond(ok) {
		inIf()
	} else if elseIfCond() {
		inElseIf()
	} else {
		inElse()
	}
	switch switchVal() {
	case caseVal():
		inCase()
	default:
		inDefault()
	}
	switch v := typeSwitchVal().(type) {
	case int:
		inTypeCase(v)
	}
	select {
	case msg := <-recvChan():
		inSelect(msg)
	case out <- sendVal():
		inSend()
	default:
		inSelectDefault()
	}
	defer deferred(deferArg())
	go spawned()
	handler := func() {
		inClosure()
	}
	for range items {
		if nestedCond() {
			inLoopIf()
		}
		go func() {
			inGoClosure()
		}()
	}
	after(handler)
	ch2 := makeClient().
		chainA().
		chainB()
	_ = ch2
	_ = andLeft() && andRight() || orRight()
}
