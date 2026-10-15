; 只收顶层声明（函数体里的局部类型不算）；方法先按顶层定义收进来，GoAdapter.CompleteFile 再挂到接收者类型下
(source_file (function_declaration name: (identifier) @name) @def.function)
(source_file (method_declaration name: (field_identifier) @name) @def.method)
(source_file (type_declaration (type_spec name: (type_identifier) @name type: (struct_type)) @def.class))
(source_file (type_declaration (type_spec name: (type_identifier) @name type: (interface_type)) @def.interface))
(source_file (type_declaration (type_spec name: (type_identifier) @name type: [(type_identifier) (qualified_type) (pointer_type) (slice_type) (array_type) (map_type) (channel_type) (function_type) (generic_type) (parenthesized_type)]) @def.type_alias))
(source_file (type_declaration (type_alias name: (type_identifier) @name) @def.type_alias))
(source_file (type_declaration (type_spec type: (interface_type (method_elem name: (field_identifier) @name) @def.method))))