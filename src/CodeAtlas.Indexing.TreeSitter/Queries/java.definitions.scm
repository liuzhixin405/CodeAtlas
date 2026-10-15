; 类型声明（含方法体里的局部类）：class / record → Class，interface / @interface → Interface，enum → Enum
(class_declaration name: (identifier) @name) @def.class
(record_declaration name: (identifier) @name) @def.class
(interface_declaration name: (identifier) @name) @def.interface
(annotation_type_declaration name: (identifier) @name) @def.interface
(enum_declaration name: (identifier) @name) @def.enum
; 方法 / 构造函数只收具名类型体里的（匿名类 new X() { ... } 里的方法不单列，调用归到外层方法）
; 限定名、StartLine（跳过注解）与 Signature 在 JavaAdapter.AdjustSymbols 里重写
(class_declaration body: (class_body (method_declaration name: (identifier) @name) @def.method))
(class_declaration body: (class_body (constructor_declaration name: (identifier) @name) @def.method))
(record_declaration body: (class_body (method_declaration name: (identifier) @name) @def.method))
(record_declaration body: (class_body (constructor_declaration name: (identifier) @name) @def.method))
(record_declaration body: (class_body (compact_constructor_declaration name: (identifier) @name) @def.method))
(enum_declaration body: (enum_body (enum_body_declarations (method_declaration name: (identifier) @name) @def.method)))
(enum_declaration body: (enum_body (enum_body_declarations (constructor_declaration name: (identifier) @name) @def.method)))
(interface_declaration body: (interface_body (method_declaration name: (identifier) @name) @def.method))
(annotation_type_declaration body: (annotation_type_body (annotation_type_element_declaration name: (identifier) @name) @def.method))
; 字段：定义节点是 variable_declarator（int a, b; 是两个字段），范围扩到整个 field_declaration；接口常量同理
(class_declaration body: (class_body (field_declaration declarator: (variable_declarator name: (identifier) @name) @def.field)))
(record_declaration body: (class_body (field_declaration declarator: (variable_declarator name: (identifier) @name) @def.field)))
(enum_declaration body: (enum_body (enum_body_declarations (field_declaration declarator: (variable_declarator name: (identifier) @name) @def.field))))
(interface_declaration body: (interface_body (constant_declaration declarator: (variable_declarator name: (identifier) @name) @def.field)))
; 记录组件 record P(Item left, …) 是 private final 字段（访问器 left() 是隐式的，解析器据此不把 p.left() 记成 external）
(record_declaration parameters: (formal_parameters (formal_parameter name: (identifier) @name) @def.field))
