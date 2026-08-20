interface ResidualReferenceRuleBase {
  readonly id: string;
  readonly term: RegExp;
  readonly path: RegExp;
  readonly owner: string;
  readonly interpretation: string;
}

export interface TemporaryResidualReferenceRule extends ResidualReferenceRuleBase {
  readonly lifecycleClass: 'protocol-migration' | 'state-migration' | 'credential-canary';
  readonly currentConsumer: string;
  readonly deletionGate: string;
  readonly milestone: 'R2' | 'R4';
}

export interface PermanentResidualReferenceRule extends ResidualReferenceRuleBase {
  readonly lifecycleClass: 'governing' | 'historical' | 'conformance';
  readonly status: string;
  readonly supersessionRule: string;
}

export type ResidualReferenceRule = TemporaryResidualReferenceRule | PermanentResidualReferenceRule;

const retiredSelector = /claude-code-cli/u;
const stateOrMarkerLegacy = /runtime_backend|runtime_provider|live_provider|legacy/iu;
const claudeBrandEvidence = /\bClaude\b(?!-code-cli\b)/iu;
export const residualReferenceDiscovery =
  /\bClaude\b|@anthropic-ai\/claude-code|\bCLAUDE_[A-Z0-9_]+\b|ClaudeCodeRuntime|ANTHROPIC_|claude_code|claude-code-cli|--resume|stream-json|runtime_backend|runtime_provider|live_provider|legacy/iu;

function permanent(
  id: string,
  path: RegExp,
  lifecycleClass: PermanentResidualReferenceRule['lifecycleClass'],
  owner: string,
  status: string,
  interpretation: string,
  supersessionRule: string,
): PermanentResidualReferenceRule {
  return {
    id,
    term: residualReferenceDiscovery,
    path,
    lifecycleClass,
    owner,
    status,
    interpretation,
    supersessionRule,
  };
}

export const residualReferenceRules = [
  {
    id: 'RR-001',
    term: retiredSelector,
    path: /^protocol\/schemas\/review-input\.v1\.json$/u,
    lifecycleClass: 'protocol-migration',
    currentConsumer: 'live embedded ReviewInputV1 schema',
    owner: 'C# RuntimeApplication protocol owner',
    interpretation:
      'historical provider vocabulary is inert schema description text, not a public compatibility route',
    deletionGate:
      'an accepted change to the live direct-runtime protocol replaces the embedded schema and C# conformance evidence',
    milestone: 'R4',
  },
  {
    id: 'RR-002',
    term: retiredSelector,
    path: /^protocol\/fixtures\/v1\//u,
    lifecycleClass: 'protocol-migration',
    currentConsumer:
      'live mixed ReviewInputV1 ReviewResultV1 ReviewTraceV1 and provider-ledger fixture corpus',
    owner: 'C# ProtocolFixtureTests and LedgerFixtureTests',
    interpretation:
      'historical provider vocabulary is synthetic fixture data validated by current C# owners',
    deletionGate:
      'an accepted protocol or ledger contract change replaces the manifest and owning C# tests',
    milestone: 'R4',
  },
  {
    id: 'RR-003',
    term: retiredSelector,
    path: /^src\/types\.ts$/u,
    lifecycleClass: 'state-migration',
    currentConsumer: 'shared root TypeScript DTO vocabulary pending W15',
    owner: 'R4-W15 issue #177',
    interpretation: 'typed migration marker outside W10 ownership',
    deletionGate: 'complete the W15 root shared-surface cleanup',
    milestone: 'R4',
  },
  {
    id: 'RR-008',
    term: stateOrMarkerLegacy,
    path: /^src\/types\.ts$/u,
    lifecycleClass: 'state-migration',
    currentConsumer: 'temporary M4 marker and state DTO vocabulary',
    owner: 'R4 Host and state bridge',
    interpretation: 'typed compatibility marker only',
    deletionGate: 'replace retained M4 TypeScript DTO surfaces',
    milestone: 'R4',
  },
  permanent(
    'RR-009',
    /^src\/artifact-provenance-vectors\.ts$/u,
    'conformance',
    'R4 S2 private artifact bridge',
    'permanent executable artifact provenance and ownership evidence',
    'direct vectors constrain the private bridge; selection and policy vectors prove that Node cannot choose state; APV-031 and APV-032 keep obsolete paths absent',
    'an accepted replacement must preserve the same S2 negative and transport evidence',
  ),
  permanent(
    'RR-014',
    /^README\.md$/u,
    'governing',
    'project maintainers',
    'current public repository boundary with historical release notes',
    'legacy terms identify the unmaintained v0.1.0 pin or removed surfaces, never a current public Action',
    'project-context.md controls current implementation status; release-policy.md controls historical tag policy',
  ),
  permanent(
    'RR-015',
    /^docs\/00_project\/project-context\.md$/u,
    'governing',
    'R2-R4 roadmap owners',
    'current project position',
    'legacy and Claude terms describe completed R1 removal or bounded migration evidence',
    'r1-legacy-removal-handoff.md owns deletion evidence; later accepted roadmap updates replace the current-position section',
  ),
  permanent(
    'RR-016',
    /^docs\/10_workflow\/release-policy\.md$/u,
    'governing',
    'release maintainers',
    'current release policy',
    'legacy terms identify the historical v0.1.0 pin and prohibit publishing an abandoned compatibility snapshot',
    'a later accepted release-policy change is required to supersede this rule',
  ),
  permanent(
    'RR-017',
    /^docs\/20_architecture\/agent-runtime-rebaseline\.md$/u,
    'governing',
    'R0-R7 architecture owners',
    'selected architecture and migration sequence',
    'legacy and Claude passages define rejected alternatives, the completed R1 boundary, or later cleanup gates',
    'project-context.md records current completion; accepted architecture amendments supersede this design',
  ),
  permanent(
    'RR-018',
    /^docs\/20_architecture\/architecture\.md$/u,
    'governing',
    'current architecture maintainers',
    'current post-R1 architecture direction with historical selector contrasts',
    'post-R1 statements govern the current tree; selector examples describe the removed mixed runtime surface and are not current configuration',
    'project-context.md and agent-runtime-rebaseline.md control sequencing; an accepted architecture revision supersedes this document',
  ),
  permanent(
    'RR-019',
    /^docs\/20_architecture\/distribution\.md$/u,
    'governing',
    'R4 and release owners',
    'current distribution transition contract',
    'legacy terms describe the historical pin and removed Claude installation surface',
    'R4 distribution design and an accepted release-policy update supersede this transition text',
  ),
  permanent(
    'RR-020',
    /^docs\/20_architecture\/m4-stateful-action\.md$/u,
    'historical',
    'R4 Host and state bridge owners',
    'retained M4 migration evidence',
    'legacy terms describe rejected sticky-state reuse, not a supported Action route',
    'R4 Host/state conformance replaces the retained M4 evidence',
  ),
  permanent(
    'RR-021',
    /^docs\/20_architecture\/r1-legacy-removal-handoff\.md$/u,
    'governing',
    'R1 handoff owner',
    'authoritative R1 deletion and transition record',
    'legacy and Claude terms record removed families, negative evidence, and owned migration inputs',
    'later milestone handoffs may supersede individual retained-family entries but not the historical deletion record',
  ),
  permanent(
    'RR-022',
    /^docs\/20_architecture\/runtime-protocol\.md$/u,
    'historical',
    'R2 protocol owner',
    'retained deterministic runtime protocol evidence',
    'legacy parser terminology contrasts the removed host path with typed protocol mapping',
    'R2 request and protocol contracts replace this evidence',
  ),
  permanent(
    'RR-023',
    /^docs\/20_architecture\/security-boundary\.md$/u,
    'governing',
    'R2-R4 security owners',
    'current security design with historical contrasts',
    'legacy terms identify behavior that does not automatically carry into the new runtime',
    'accepted security-boundary revisions supersede individual historical comparisons',
  ),
  permanent(
    'RR-024',
    /^docs\/20_architecture\/session-ledger-and-prefix-contract\.md$/u,
    'historical',
    'R4 Host, ledger, and state owners',
    'retained M4 contract and migration evidence',
    'legacy and selector terms define unsupported inputs, removed wire spellings, and non-replay behavior',
    'R4 Host/ledger/state conformance replaces executable ownership; this file remains historical evidence',
  ),
  permanent(
    'RR-025',
    /^docs\/20_architecture\/state-manifest-v2\.md$/u,
    'historical',
    'R4 state bridge owner',
    'retired v2 state-bundle conformance evidence',
    'legacy terms name the former unsupported-v1 classification as historical evidence',
    'W5 removal replaces executable ownership; this file remains historical evidence',
  ),
  permanent(
    'RR-026',
    /^docs\/50_ai\/agent-context\.md$/u,
    'governing',
    'repository agent-workflow maintainers',
    'current agent startup context',
    'legacy and Claude terms state the completed R1 boundary and classify older contracts as migration or historical evidence',
    'project-context.md and accepted milestone handoffs control current implementation status',
  ),
  permanent(
    'RR-027',
    /^docs\/50_ai\/skills\/runtime-design-refinement\.md$/u,
    'governing',
    'runtime design-refinement maintainers',
    'current design procedure',
    'legacy terms prohibit manufacturing a new release solely to preserve abandoned code',
    'release-policy.md controls release decisions; an accepted skill revision supersedes this procedure',
  ),
  permanent(
    'RR-028',
    /^docs\/90_roadmap\/m3-m6-plan\.md$/u,
    'historical',
    'roadmap maintainers',
    'superseded pre-rebaseline roadmap',
    'legacy selectors and Claude decisions describe the earlier M3-M6 sequence, not current implementation',
    'roadmap-seed.md and project-context.md control current sequencing',
  ),
  permanent(
    'RR-029',
    /^docs\/90_roadmap\/roadmap-seed\.md$/u,
    'governing',
    'R0-R7 roadmap owners',
    'current milestone sequence with completed R1 clauses',
    'legacy and Claude terms define the completed R1 scope, historical pin, or later cleanup boundaries',
    'project-context.md records current completion; accepted roadmap amendments supersede future sequencing',
  ),
  permanent(
    'RR-030',
    /^docs\/50_ai\/collaboration-layers\.md$/u,
    'governing',
    'repository collaboration-policy maintainers',
    'current layered collaboration and agent-entrypoint policy',
    'Claude references identify the supported thin Claude-specific entrypoint and future agent-specific directory, not a provider/runtime execution path',
    'an accepted collaboration-policy revision supersedes this rule',
  ),
  {
    id: 'RR-031',
    term: claudeBrandEvidence,
    path: /^protocol\/fixtures\/v1\//u,
    lifecycleClass: 'protocol-migration',
    currentConsumer: 'live synthetic protocol and ledger provider-model fixtures',
    owner: 'C# ProtocolFixtureTests and LedgerFixtureTests',
    interpretation:
      'provider and model identity is inert fixture evidence, not an executable Claude runtime route',
    deletionGate:
      'an accepted protocol or ledger contract change replaces the manifest and owning C# tests',
    milestone: 'R4',
  },
  permanent(
    'RR-034',
    /^CLAUDE\.md$/u,
    'governing',
    'repository agent-workflow maintainers',
    'current thin Claude-specific contributor and agent entrypoint',
    'Claude references define repository instruction routing only, not provider/runtime execution',
    'AGENTS.md and docs/50_ai/collaboration-layers.md govern its scope; an accepted collaboration-policy revision supersedes this entrypoint',
  ),
  permanent(
    'RR-037',
    /^docs\/20_architecture\/r3-single-shot-removal-handoff\.md$/u,
    'historical',
    'R3 live-Agent replacement owner',
    'checked single-shot deletion and later-consumer handoff',
    'legacy vocabulary occurs only in the readable R1 handoff link; retired runtime names and selectors are inventory or negative evidence',
    'an accepted later-milestone handoff may supersede retained-consumer ownership but not the R3 deletion record',
  ),
  permanent(
    'RR-038',
    /^runtime\/tests\/fixtures\/action-host\/framework\/(?:e1-base-inventory|replacement-record)\.json$/u,
    'conformance',
    'R4 E1 framework verifier',
    'checked framework source inventory and deletion replacement evidence',
    'legacy vocabulary occurs only in the exact path of the authoritative R1 historical handoff consumed by W3 replacement validation',
    'W13 may replace this inventory and record only after preserving the same historical-reference ownership and framework proof',
  ),
  permanent(
    'RR-039',
    /^runtime\/tests\/AgenticPrReview\.Runtime\.Tests\/Host\/Action\/ActionHostFrameworkVerifierArchitectureTests\.cs$/u,
    'conformance',
    'R4 E1 framework verifier architecture tests',
    'exact W3 and W11 replacement-record assertions',
    'legacy vocabulary occurs only in the authoritative R1 handoff path retained by the exact W3 reference-set assertion',
    'W13 may replace this assertion only after preserving the same historical-reference ownership and framework proof',
  ),
  permanent(
    'RR-040',
    /^runtime\/tests\/ActionHostVerifierFixture\/FrameworkSupervisor\.cs$/u,
    'conformance',
    'R4 E1 framework verifier',
    'W6 exact deleted-route and replacement-evidence assertions',
    'legacy vocabulary occurs only in negative deletion checks and the closed W6 historical-test manifest',
    'W13 may replace this assertion only after preserving the same W6 absence proof and mapped replacement evidence',
  ),
] as const satisfies readonly ResidualReferenceRule[];
