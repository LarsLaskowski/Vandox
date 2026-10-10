# Security

**Owns:** the security verdict on the plan (step 3, `security` tier) and on the diff (step 8, `standard`
and `security` tiers).

Focus areas: the *Security areas* in `.squad/project.md` (this project's attack surface — secrets,
authentication, file writes, parsing of external input, outbound calls, logging of external data, …),
CI/Docker/build configuration defaults, and new or updated dependencies.

For a plan that adds or tightens a guard against bypasses, enumerate the forms the guarded input's real
parser accepts and report every missed bypass class in one round, not one per round.

When a plan claims bounded memory, the plan review asks what retains derived values — substrings or slices
that pin a larger buffer, cached keys, error text built from input — and the Tester needs a heap-bound test
for each such claim (*Memory claims* in its charter).

Answer with `APPROVED` or `CHANGES_REQUIRED`, each required change concrete and backed by evidence (file
and line, or the plan passage). No speculative or generic advice.
