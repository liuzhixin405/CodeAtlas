; 调用：裸名字 f() 与 x.f()（x 是包名、变量或表达式，GoAdapter 按接收者补绑定）
(call_expression function: (identifier) @ref.calls)
(call_expression function: (selector_expression field: (field_identifier) @ref.calls))
; 泛型调用 F[T](x) / pkg.F[T](x)：tree-sitter 解析成类型转换，这里当调用收（F 其实是泛型类型时解析器改记 references）
(type_conversion_expression type: (generic_type type: (type_identifier) @ref.calls))
(type_conversion_expression type: (generic_type type: (qualified_type name: (type_identifier) @ref.calls)))
; 函数值实参：router.POST("", Handler)、http.HandleFunc("/", srv.Handle)（GoAdapter 去掉局部变量，记 Via = funcvalue）
(argument_list (identifier) @ref.references)
(argument_list (selector_expression field: (field_identifier) @ref.references))
; 类型引用：参数、返回值、具名字段、var 声明、复合字面量（GoAdapter 取出 *T / []T / pkg.T 里的命名类型）
(parameter_declaration type: (_) @ref.references)
(variadic_parameter_declaration type: (_) @ref.references)
(field_declaration name: (field_identifier) type: (_) @ref.references)
(var_spec type: (_) @ref.references)
(composite_literal type: (_) @ref.references)
(function_declaration result: (_) @ref.references)
(method_declaration result: (_) @ref.references)
(method_elem result: (_) @ref.references)
; 内嵌：struct 的匿名字段（带名字的字段由 GoAdapter 否决）与 interface 的内嵌接口
(struct_type (field_declaration_list (field_declaration type: (_) @ref.inherits)))
(interface_type (type_elem (type_identifier) @ref.inherits))
(interface_type (type_elem (qualified_type) @ref.inherits))
(import_spec path: (interpreted_string_literal (interpreted_string_literal_content) @ref.imports))