(class_declaration name: (identifier) @name) @def.class
(function_declaration name: (identifier) @name) @def.function
(method_definition name: (property_identifier) @name) @def.method
(field_definition property: (property_identifier) @name) @def.field
(variable_declarator name: (identifier) @name value: [(arrow_function) (function_expression)]) @def.function
