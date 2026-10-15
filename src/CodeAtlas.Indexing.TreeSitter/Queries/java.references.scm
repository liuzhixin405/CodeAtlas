; 调用：x.m(…) / m(…)、new T(…)、方法引用 X::m（接收者与实参个数在 JavaAdapter.AnnotateReference 里补）
(method_invocation name: (identifier) @ref.calls)
(object_creation_expression type: (_) @ref.calls)
; 方法引用取最后一个标识符；Foo::new 的最后一个具名子节点是 Foo 本身，由 JavaAdapter.AcceptReference 否决
(method_reference (identifier) @ref.calls .)
; extends（类的父类、接口的父接口）→ inherits；implements → implements
(superclass (_) @ref.inherits)
(extends_interfaces (type_list (_) @ref.inherits))
(super_interfaces (type_list (_) @ref.implements))
; 类型引用：字段 / 参数 / 返回值 / 局部变量的类型（含泛型实参、数组元素）；上下文与类型参数由 JavaAdapter.AcceptReference 过滤
(type_identifier) @ref.references
(scoped_type_identifier) @ref.references
; import 不在这里捕获：JavaAdapter.CompleteFile 直接读 import_declaration（通配 .* 与 static 都要看兄弟节点）
