(namespace_declaration name: (_) @name) @def.namespace
(file_scoped_namespace_declaration name: (_) @name) @def.namespace
(class_declaration name: (identifier) @name) @def.class
(record_declaration name: (identifier) @name) @def.class
(struct_declaration name: (identifier) @name) @def.struct
(interface_declaration name: (identifier) @name) @def.interface
(enum_declaration name: (identifier) @name) @def.enum
(delegate_declaration name: (identifier) @name) @def.type_alias
(method_declaration name: (identifier) @name) @def.method
(constructor_declaration name: (identifier) @name) @def.method
(property_declaration name: (identifier) @name) @def.property
(field_declaration (variable_declaration (variable_declarator name: (identifier) @name) @def.field))
(local_function_statement name: (identifier) @name) @def.function
