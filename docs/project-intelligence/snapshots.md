# Project Snapshots

A snapshot captures enough project state to detect stale context and meaningful engineering changes.

Include:

- project ID
- TIA version
- timestamp
- artifact identities
- source/content hashes
- tags
- hardware metadata
- graph metadata

A write planned against an old snapshot must be rejected when the actual project no longer matches.
