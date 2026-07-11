# Deterministic Runtime CLI

This directory contains the M2 C# runtime implemented by issue #19. Its only command is:

```text
review --input <path> --output <path> --trace <path>
```

Use the repository SDK baseline and run the runtime tests with:

```bash
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj
```

The production validator is `JsonSchema.Net` 9.2.2. The three authoritative V1 schemas are
embedded resources; runtime input and generated result/trace bytes are evaluated directly against
those schemas. The package does not mark its assembly `IsAotCompatible`, so the .NET AOT analyzer
emits IL3058. This is individually justified by the required publish-and-execute proof below: the
published Native AOT binary evaluates all embedded schemas and completes the deterministic fixture
path. Do not replace this with a hand-written partial validator.

On Linux, the required feasibility proof is:

```bash
publish_dir="$(mktemp -d)"
smoke_dir="$(mktemp -d)"
dotnet publish runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj \
  -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o "$publish_dir"
cp protocol/fixtures/v1/cases/bootstrap/input.json "$smoke_dir/input.json"
"$publish_dir/AgenticPrReview.Runtime" review \
  --input "$smoke_dir/input.json" \
  --output "$smoke_dir/result.json" \
  --trace "$smoke_dir/trace.json"
cmp "$smoke_dir/trace.json" runtime/tests/fixtures/deterministic/bootstrap/expected-trace.json
cmp "$smoke_dir/result.json" runtime/tests/fixtures/deterministic/bootstrap/expected-result.json
```

Issue #21 owns making this framework-dependent and Native AOT path recurring CI.
