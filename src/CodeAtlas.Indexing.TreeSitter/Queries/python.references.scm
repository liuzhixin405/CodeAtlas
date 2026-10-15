; 调用：f()、obj.f()（接收者在 PythonAdapter.AnnotateReference 里看）
(call function: (identifier) @ref.calls)
(call function: (attribute attribute: (identifier) @ref.calls))
; 基类：class A(B, mod.C)；metaclass= / table=True 这类关键字实参不是基类，Generic[T] 也不记
(class_definition superclasses: (argument_list (identifier) @ref.inherits))
(class_definition superclasses: (argument_list (attribute attribute: (identifier) @ref.inherits)))
; 类型注解：x: T、-> T、list[T] 里的 T、mod.T、T | None；字符串注解 -> "User" / x: "models.User"（PythonAdapter 只收点分名字）
(type (identifier) @ref.references)
(type (attribute attribute: (identifier) @ref.references))
(type (binary_operator left: (identifier) @ref.references))
(type (binary_operator right: (identifier) @ref.references))
(type (string (string_content) @ref.references))
; 类型别名的右边：Annotated[T, …] / Optional[T] / Union[A, B] 里的类型（引用归别名符号）
(module (assignment left: (identifier) right: (subscript value: (identifier) @_alias_head subscript: (identifier) @ref.references)) (#match? @_alias_head "^(Annotated|Optional|Union)$"))
(module (assignment left: (identifier) right: (subscript value: (identifier) @_alias_head subscript: (attribute attribute: (identifier) @ref.references))) (#match? @_alias_head "^(Annotated|Optional|Union)$"))
; Django ORM：Article.objects.filter(…) 的 Article 记一条引用（方法本身仍是 external）
(attribute object: (identifier) @ref.references attribute: (identifier) @_manager (#eq? @_manager "objects"))
; 类属性别名的右边：Field → 被别名的类
(class_definition body: (block (assignment left: (identifier) right: (identifier) @ref.references)))
(class_definition body: (block (assignment left: (identifier) right: (attribute attribute: (identifier) @ref.references))))
; import x.y / import x.y as z / from x import y / from . import y / from ..core import y
(import_statement name: (dotted_name) @ref.imports)
(import_statement name: (aliased_import name: (dotted_name) @ref.imports))
(import_from_statement module_name: (_) @ref.imports)
