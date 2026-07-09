# Issue Refinement

Use this procedure when turning an idea, placeholder, or broad request into an agent-ready issue.

Follow `docs/10_workflow/issue-workflow.md` for issue type selection and body conventions.

## Inputs

- the current issue or task prompt;
- repository docs;
- code paths;
- PRs or release notes.

If important context is missing, ask for a clarification.

## Preflight Decisions

Do not hide high-impact decisions inside a long issue draft. Surface them first when the task changes:

- action inputs or outputs;
- JSON schema or runtime protocol;
- runtime/provider naming or responsibility boundary;
- GitHub token or side-effect boundary;
- state artifact or memory model;
- comment publishing behavior;
- release, pinning, or runtime download policy;
- failure mode or fail-closed behavior;
- trace or artifact privacy contract.

For each decision, provide:

- recommended option;
- alternatives;
- why;
- impact if wrong;
- whether human confirmation is needed.

## Agent-Ready Criteria

An issue is agent-ready when:

- objective is clear;
- acceptance criteria are clear;
- relevant docs or code paths are linked;
- no unresolved design questions remain;
- validation method is defined;
- scope fits one focused PR.

If a human decision is still needed, state that design is still needed and name the blocking decision.

## Draft Shape

Keep body sections clear and execution-oriented:

- objective or goal;
- context;
- scope;
- expected output;
- acceptance criteria;
- related docs, issues, or PRs;
- notes.
