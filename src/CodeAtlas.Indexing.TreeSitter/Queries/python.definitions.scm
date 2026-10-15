; 定义节点取 class_definition / function_definition 本身（不是外层的 decorated_definition），StartLine 落在 def / class 那一行
; 类体里的函数在 PythonAdapter.AdjustSymbols 里改成 Method
(class_definition name: (identifier) @name) @def.class
(function_definition name: (identifier) @name) @def.function
; 类属性别名 serializer_class = ArticleSerializer：右边是名字 / 点分名字才记（字符串、调用、元组都不记），self.serializer_class() 据此解析
(class_definition body: (block (assignment left: (identifier) @name right: [(identifier) (attribute)]) @def.field))
; 模块级类型别名：X = Annotated[T, Depends(…)] / Optional[T] / Union[A, B]、type X = T（右边的类型是它的 references，解析器按它找方法）
(module (assignment left: (identifier) @name right: (subscript value: (identifier) @_alias_head)) @def.type_alias (#match? @_alias_head "^(Annotated|Optional|Union)$"))
(module (type_alias_statement . (type (identifier) @name)) @def.type_alias)
