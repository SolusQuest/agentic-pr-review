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
dotnet publish runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj \
  -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o /tmp/apr-runtime-aot
mkdir -p /tmp/apr-runtime-smoke
cp protocol/fixtures/v1/cases/bootstrap/input.json /tmp/apr-runtime-smoke/input.json
/tmp/apr-runtime-aot/AgenticPrReview.Runtime review \
  --input /tmp/apr-runtime-smoke/input.json \
  --output /tmp/apr-runtime-smoke/result.json \
  --trace /tmp/apr-runtime-smoke/trace.json
```

Issue #21 owns making this framework-dependent and Native AOT path recurring CI.
