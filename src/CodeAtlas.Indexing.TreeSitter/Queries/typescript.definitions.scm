(class_declaration name: (type_identifier) @name) @def.class
(abstract_class_declaration name: (type_identifier) @name) @def.class
(interface_declaration name: (type_identifier) @name) @def.interface
(enum_declaration name: (identifier) @name) @def.enum
(type_alias_declaration name: (type_identifier) @name) @def.type_alias
(internal_module name: (identifier) @name) @def.namespace
(function_declaration name: (identifier) @name) @def.function
; async function* g() {}
(generator_function_declaration name: (identifier) @name) @def.function
; 方法（含私有 #m()）；getter 记为属性，setter 不单独出符号（与 getter 同名）
((method_definition name: [(property_identifier) (private_property_identifier)] @name) @def.method (#not-match? @def.method "^((public|private|protected|static|override|async)\\s+)*(get|set)\\s+[^\\s(]"))
(method_definition "get" name: [(property_identifier) (private_property_identifier)] @name) @def.property
(method_signature name: (property_identifier) @name) @def.method
(abstract_method_signature name: (property_identifier) @name) @def.method
(public_field_definition name: [(property_identifier) (private_property_identifier)] @name) @def.field
(property_signature name: (property_identifier) @name) @def.property
(variable_declarator name: (identifier) @name value: [(arrow_function) (function_expression) (generator_function)]) @def.function
(export_statement value: (arrow_function) @def.function @name.default)
(export_statement value: (function_expression) @def.function @name.default)
(export_statement value: (class) @def.class @name.default)
(program (lexical_declaration (variable_declarator name: (identifier) @name value: (call_expression arguments: (arguments [(arrow_function) (function_expression)]))) @def.function))
(program (export_statement (lexical_declaration (variable_declarator name: (identifier) @name value: (call_expression arguments: (arguments [(arrow_function) (function_expression)]))) @def.function)))
; 两层包装（外层限 memo / forwardRef / observer，免得 Object.fromEntries(arr.map(…)) 这类被当成函数）：const X = memo(forwardRef((props, ref) => …))；以及 forwardRef(…) as …
((program (lexical_declaration (variable_declarator name: (identifier) @name value: (call_expression function: (_) @_w arguments: (arguments (call_expression arguments: (arguments [(arrow_function) (function_expression)]))))) @def.function)) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
((program (export_statement (lexical_declaration (variable_declarator name: (identifier) @name value: (call_expression function: (_) @_w arguments: (arguments (call_expression arguments: (arguments [(arrow_function) (function_expression)]))))) @def.function))) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
(program (lexical_declaration (variable_declarator name: (identifier) @name value: (as_expression (call_expression arguments: (arguments [(arrow_function) (function_expression)])))) @def.function))
(program (export_statement (lexical_declaration (variable_declarator name: (identifier) @name value: (as_expression (call_expression arguments: (arguments [(arrow_function) (function_expression)])))) @def.function)))
; 包装别名：const X = memo(Y) / forwardRef(Y) / observer(Y)（可带 as …），X 是变量，引用里记一条 X 渲染 Y
((program (lexical_declaration (variable_declarator name: (identifier) @name value: (call_expression function: (_) @_w arguments: (arguments . (identifier) .))) @def.variable)) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
((program (export_statement (lexical_declaration (variable_declarator name: (identifier) @name value: (call_expression function: (_) @_w arguments: (arguments . (identifier) .))) @def.variable))) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
((program (lexical_declaration (variable_declarator name: (identifier) @name value: (as_expression (call_expression function: (_) @_w arguments: (arguments . (identifier) .)))) @def.variable)) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
((program (export_statement (lexical_declaration (variable_declarator name: (identifier) @name value: (as_expression (call_expression function: (_) @_w arguments: (arguments . (identifier) .)))) @def.variable))) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
; export default memo(function X() {}) / memo(forwardRef(function X() {}))：以内部函数名命名
(export_statement value: (call_expression arguments: (arguments (function_expression name: (identifier) @name))) @def.function)
(export_statement value: (call_expression arguments: (arguments (call_expression arguments: (arguments (function_expression name: (identifier) @name))))) @def.function)
; export default memo(() => …)：匿名，适配器改用文件名
((export_statement value: (call_expression function: (_) @_w arguments: (arguments [(arrow_function) (function_expression !name)])) @def.function @name.default) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
((export_statement value: (call_expression function: (_) @_w arguments: (arguments (call_expression arguments: (arguments [(arrow_function) (function_expression !name)])))) @def.function @name.default) (#match? @_w "^((React\\.)?(memo|forwardRef)|observer)$"))
; CommonJS：module.exports = function charge() {} / function () {} / () => … / class X {}（默认导出，适配器标记）
((expression_statement (assignment_expression left: (member_expression object: (identifier) @_m property: (property_identifier) @_e) right: (function_expression name: (identifier) @name) @def.function)) (#eq? @_m "module") (#eq? @_e "exports"))
((expression_statement (assignment_expression left: (member_expression object: (identifier) @_m property: (property_identifier) @_e) right: [(arrow_function) (function_expression !name)] @def.function @name.default)) (#eq? @_m "module") (#eq? @_e "exports"))
((expression_statement (assignment_expression left: (member_expression object: (identifier) @_m property: (property_identifier) @_e) right: (class name: (_) @name) @def.class)) (#eq? @_m "module") (#eq? @_e "exports"))
; CommonJS：exports.f = … / module.exports.f = … / module.exports = { f: () => … }（具名导出）
((assignment_expression left: (member_expression object: (identifier) @_e property: (property_identifier) @name) right: [(arrow_function) (function_expression) (generator_function)]) @def.function (#eq? @_e "exports"))
((assignment_expression left: (member_expression object: (member_expression object: (identifier) @_m property: (property_identifier) @_e) property: (property_identifier) @name) right: [(arrow_function) (function_expression) (generator_function)]) @def.function (#eq? @_m "module") (#eq? @_e "exports"))
((assignment_expression left: (member_expression object: (identifier) @_m property: (property_identifier) @_e) right: (object (pair key: (property_identifier) @name value: [(arrow_function) (function_expression)]) @def.function)) (#eq? @_m "module") (#eq? @_e "exports"))
