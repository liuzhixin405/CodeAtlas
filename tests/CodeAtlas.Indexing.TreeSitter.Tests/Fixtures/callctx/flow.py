# 调用大纲夹具：每个调用名唯一，测试逐条断言它所在的控制结构
def run(xs):
    setup()
    for x in for_src():
        in_for(x)
    else:
        for_else()
    while while_cond():
        in_while()
    if if_cond():
        in_if()
    elif elif_cond():
        in_elif()
    else:
        in_else()
    v = tern_a() if tern_cond() else tern_b()
    ys = [list_body(x) for x in list_src() if list_filter(x)]
    zs = {dict_key(x): dict_val(x) for x in dict_src()}
    ss = {set_body(x) for x in set_src()}
    total = gen_sum(gen_body(x) for x in gen_src())
    try:
        in_try()
    except ValueError:
        in_except()
    else:
        in_try_else()
    finally:
        in_finally()
    match match_subject():
        case [p]:
            in_case()
        case _ if case_guard():
            in_guard_case()
    cb = lambda q: in_lambda(q)
    for y in xs:
        if loop_if_cond():
            in_loop_if()
        try:
            in_loop_try()
        except KeyError:
            in_loop_except()
    after(cb)
    xs.chain_a(
    ).chain_b()
    flag = and_left() and and_right() or or_right()

    def nested():
        in_nested()
    return nested
