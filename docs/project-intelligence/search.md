# Project Search

Phase 2 starts with deterministic local search.

Search dimensions:

- exact name
- path
- type
- tags
- block source
- comments
- cross references
- dependencies

Keep `IProjectSearchProvider` abstract so semantic/vector retrieval can be added later without changing Engineering Engine contracts.
