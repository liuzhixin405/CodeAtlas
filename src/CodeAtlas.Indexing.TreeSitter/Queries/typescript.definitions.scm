(class_declaration name: (type_identifier) @name) @def.class
(abstract_class_declaration name: (type_identifier) @name) @def.class
(interface_declaration name: (type_identifier) @name) @def.interface
(enum_declaration name: (identifier) @name) @def.enum
(type_alias_declaration name: (type_identifier) @name) @def.type_alias
(internal_module name: (identifier) @name) @def.namespace
(function_declaration name: (identifier) @name) @def.function
(method_definition name: (property_identifier) @name) @def.method
(method_signature name: (property_identifier) @name) @def.method
(abstract_method_signature name: (property_identifier) @name) @def.method
(public_field_definition name: (property_identifier) @name) @def.field
(property_signature name: (property_identifier) @name) @def.property
(variable_declarator name: (identifier) @name value: [(arrow_function) (function_expression)]) @def.function
