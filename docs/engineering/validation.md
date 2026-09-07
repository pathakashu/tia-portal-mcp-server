# Validation

Validation stages:

1. Schema validation
2. Engineering IR validation
3. Naming validation
4. Reference validation
5. Data type validation
6. Dependency/impact validation
7. TIA V18 capability validation
8. Post-execution state validation
9. Compile validation

Validation should be deterministic and independently testable.

The constrained SCL preview validates the existing create-block intent, requires `Scl` language and `Function` or `FunctionBlock` type, and accepts only identifier-form parameter data types. Its typed assignments can only target declared outputs and source declared input or output identifiers. It does not accept a free-form SCL source body, expressions, or control flow.
