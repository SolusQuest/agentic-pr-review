using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed class AgentContractTests
{
    [Fact]
    public void LimitsRegistryIsTheCompleteFrozenAuthority()
    {
        var expected = new (string Name, long Value, string Unit)[]
        {
            ("model_calls", 8, "count"),
            ("tool_calls", 24, "count"),
            ("tool_calls_per_response", 8, "count"),
            ("concurrent_tool_calls", 1, "count"),
            ("deadline_seconds", 300, "seconds"),
            ("input_tokens", 262_144, "tokens"),
            ("output_tokens", 32_768, "tokens"),
            ("combined_tokens", 294_912, "tokens"),
            ("request_bytes", 1_048_576, "bytes"),
            ("response_bytes", 1_048_576, "bytes"),
            ("messages", 64, "count"),
            ("parts_per_message", 32, "count"),
            ("parts_total", 256, "count"),
            ("content_bytes", 65_536, "bytes"),
            ("tool_arguments_bytes", 8_192, "bytes"),
            ("tool_result_bytes", 32_768, "bytes"),
            ("tool_results_total_bytes", 262_144, "bytes"),
            ("read_file_raw_bytes", 65_536, "bytes"),
            ("read_file_lines", 400, "lines"),
            ("search_files", 100, "count"),
            ("search_raw_bytes", 8_388_608, "bytes"),
            ("search_file_bytes", 262_144, "bytes"),
            ("search_matches", 100, "count"),
            ("path_bytes", 1_024, "bytes"),
            ("query_bytes", 4_096, "bytes"),
            ("findings", 20, "count"),
            ("summary_bytes", 8_192, "bytes"),
            ("finding_title_bytes", 512, "bytes"),
            ("finding_message_bytes", 16_384, "bytes"),
            ("evidence_per_finding", 8, "count"),
            ("terminal_bytes", 262_144, "bytes"),
            ("session_records", 256, "count"),
            ("session_record_bytes", 524_288, "bytes"),
            ("continuation_item_bytes", 65_536, "bytes"),
            ("continuation_total_bytes", 262_144, "bytes"),
            ("session_plaintext_bytes", 1_048_576, "bytes"),
            ("state_envelope_bytes", 2_097_152, "bytes"),
            ("accepted_candidates", 2, "count"),
            ("candidate_metadata_bytes", 16_384, "bytes"),
            ("candidate_envelope_total_bytes", 4_194_304, "bytes"),
            ("state_scope_total_bytes", 6_291_456, "bytes"),
            ("tracked_files", 20_000, "count"),
            ("tracked_files_metadata_bytes", 8_388_608, "bytes"),
            ("list_files_entries", 100, "count"),
            ("list_changed_files_entries", 100, "count"),
            ("changed_files", 200, "count"),
            ("changed_files_metadata_bytes", 262_144, "bytes"),
            ("diff_hunks_per_file", 200, "count"),
            ("diff_lines_per_hunk", 1_000, "lines"),
            ("diff_line_text_bytes", 4_096, "bytes"),
            ("diff_source_bytes_per_file", 524_288, "bytes"),
            ("diff_snapshot_bytes", 8_388_608, "bytes"),
        };

        Assert.Equal(52, AgentLimits.Registry.Length);
        Assert.Equal(
            Enumerable.Range(1, 47).Concat(Enumerable.Range(49, 5)),
            AgentLimits.Registry.Select(row => row.Ordinal));
        Assert.Equal(
            expected,
            AgentLimits.Registry.Select(row =>
                (row.Name, row.Value, row.Unit)));
    }

    [Theory]
    [InlineData("apr.limits.r2", "7e668195c0b61e7f4bace357ead81d03df6baa17d6319e350391fd7623bcc42e")]
    [InlineData("apr.toolset.r2", "14075edaa6a137ace8019f5adda76600d3e43038a4eb7c8497061d983be7f538")]
    [InlineData("apr.stable-plan.r2", "ebbb12dafb8488f38cd02dd86dbf8b18accbdc770ee5a32a9e7771dfab4acb69")]
    [InlineData("apr.query.r2", "7dea65bf0eb5067d3983f64cec21183229ed106f0d44b0371822ae5017fbe92c")]
    [InlineData("apr.observation.read.r2", "29e82b21b618e8859c7e1d8a54784bc843e66bce498274fe705e6824c8fb9cc1")]
    [InlineData("apr.observation.search.r2", "8eb931687a9850ec01aa8cc526dd8c7a845ccba9f4b3109c8ff038c9285497a8")]
    [InlineData("apr.observation.list-files.r3", "0dafdac15bfa06c382fa561d45135ccecec3c26ae48ec96855607b0d18ca33d0")]
    [InlineData("apr.diff-source.r3", "473c5f08ba3a85b7c9998363f958d238b90bbcc514dcaebb0f5c4ced42dbe86e")]
    [InlineData("apr.observation.list-changed-files.r3", "b925be602c32373267a1ce761d3fd6aa81e5a1a1c439b39ef90b54c7fb318d62")]
    [InlineData("apr.terminal.r2", "62fdb2e3884625096b9d88c93167b2f79ffe57f1c4e5bd56da3b3c1ea952f705")]
    [InlineData("apr.continuation.r2", "39712cc1233e5beafe94ca09fd714f576bb94db59867755abac8681556ef7854")]
    [InlineData("apr.session.r2", "09fdc5c02229991e8830926ea79ca9ee4052dde3d7da179260af013d71830783")]
    [InlineData("apr.state-envelope.r2", "f83b9a033af584c5e830d3b0ff413519d53216f1de089e356d3610232f85d8fd")]
    public void DomainSeparatedHashMatchesAlgorithmGolden(
        string domain,
        string expected)
    {
        Assert.Equal(
            expected,
            AgentCanonical.HashDomain(domain, "{}"u8));
    }

    [Fact]
    public void ToolsetUsesExactOrderDescriptionsAndSchemas()
    {
        Assert.Collection(
            AgentToolRegistry.Definitions,
            list =>
            {
                Assert.Equal("list_files", list.Name);
                Assert.Equal(
                    "List tracked repository paths from the reviewed snapshot in ordinal order.",
                    list.Description);
                Assert.Equal(AgentToolRegistry.ListFilesSchema, list.SchemaJson);
            },
            changed =>
            {
                Assert.Equal("list_changed_files", changed.Name);
                Assert.Equal(
                    "List bounded changed-file metadata from the reviewed snapshot in ordinal path order.",
                    changed.Description);
                Assert.Equal(
                    AgentToolRegistry.ListChangedFilesSchema,
                    changed.SchemaJson);
            },
            search =>
            {
                Assert.Equal("search_text", search.Name);
                Assert.Equal(
                    "Search for a case-sensitive literal in tracked UTF-8 files in the reviewed snapshot.",
                    search.Description);
                Assert.Equal(AgentToolRegistry.SearchTextSchema, search.SchemaJson);
            },
            read =>
            {
                Assert.Equal("read_file", read.Name);
                Assert.Equal(
                    "Read a bounded line range from one tracked UTF-8 file in the reviewed snapshot.",
                    read.Description);
                Assert.Equal(AgentToolRegistry.ReadFileSchema, read.SchemaJson);
            },
            finish =>
            {
                Assert.Equal("finish_review", finish.Name);
                Assert.Equal(
                    "Finish the review with validated grounded findings.",
                    finish.Description);
                Assert.Equal(AgentToolRegistry.FinishReviewSchema, finish.SchemaJson);
            });

        Assert.Equal(
            "09deb3e4db4b6ade240903fcdbe56a65ad863658f0ead46d096b5e3e18c7ad68",
            AgentCanonical.LimitsSha256());
        Assert.Equal(
            "cfd005d61d68dee38a4a539612c6f650bd0001b7b1223006cd1d14a0b75ddd88",
            AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions));

        var original = AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions);
        var definitions = AgentToolRegistry.Definitions.ToArray();
        Assert.NotEqual(
            original,
            AgentCanonical.ToolsetSha256(definitions.Reverse().ToArray()));
        Assert.NotEqual(
            original,
            AgentCanonical.ToolsetSha256(
                definitions.Select((tool, index) =>
                    index == 0 ? tool with { Name = "list-files" } : tool).ToArray()));
        Assert.NotEqual(
            original,
            AgentCanonical.ToolsetSha256(
                definitions.Select((tool, index) =>
                    index == 1 ? tool with { Description = tool.Description + " " } : tool)
                    .ToArray()));
        Assert.NotEqual(
            original,
            AgentCanonical.ToolsetSha256(
                definitions.Select((tool, index) =>
                    index == 3 ? tool with { SchemaJson = "{}" } : tool).ToArray()));
    }

    [Fact]
    public void StablePlanDigestBindsEveryStableFieldAndNothingTransient()
    {
        var plan = new StableAgentPlan(
            "repo",
            1,
            "workflow",
            new string('0', 64),
            new string('1', 64),
            new string('2', 64),
            "build",
            "provider",
            "model",
            "adapter",
            null);
        var original = AgentCanonical.StablePlanSha256(plan);
        Assert.Equal(
            "96fb3f948c5aff2be85cf85055193c90c42554ddac205dc175cfd6f7dd889518",
            original);
        var mutations = new[]
        {
            plan with { RepositoryId = "repo-2" },
            plan with { ReviewTarget = 2 },
            plan with { WorkflowIdentity = "workflow-2" },
            plan with { PolicySha256 = new string('3', 64) },
            plan with { ToolsetSha256 = new string('3', 64) },
            plan with { LimitsSha256 = new string('3', 64) },
            plan with { BuildId = "build-2" },
            plan with { ProviderId = "provider-2" },
            plan with { ModelId = "model-2" },
            plan with { AdapterId = "adapter-2" },
            plan with { PriorSessionSha256 = new string('4', 64) },
        };

        Assert.All(
            mutations,
            mutation => Assert.NotEqual(
                original,
                AgentCanonical.StablePlanSha256(mutation)));
    }

    [Fact]
    public void CanonicalStringsUseFrozenEscapesAndRawUnicode()
    {
        var bytes = AgentCanonical.QueryBytes("é/\b\u0001");
        Assert.Equal(
            "{\"query\":\"é/\\b\\u0001\"}",
            Encoding.UTF8.GetString(bytes));
    }
}
