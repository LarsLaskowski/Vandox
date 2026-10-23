# memfd write measurement (Product Manager, 2026-10-23)

Test: `target` (root) maps a memfd `MAP_SHARED` and prints its first bytes; `probe` runs as `nobody` (capsh, ambient caps), opens `/proc/<pid>/fd/<fd>` `O_RDWR` and `pwrite`s `BBBB`.

| Host | Kernel | no caps | CAP_SYS_PTRACE (CapEff 0x80000) | CAP_SYS_PTRACE + CAP_DAC_READ_SEARCH (0x80004) |
|---|---|---|---|---|
| NAS | 4.4.302+ | open EACCES | open EACCES | open ok, pwrite 4 bytes, target shows BBBB |
| Ubuntu 22.04.5 | 5.15.0-198-generic | open EACCES | open EACCES | open ok, pwrite 4 bytes, target shows BBBB |
| Ubuntu 24.04.5 | 6.8.0-147-generic | open EACCES | open EACCES | open ok, pwrite 4 bytes, target shows BBBB |
| Ubuntu 26.04.1 | 7.0.0-38-generic | open EACCES | open EACCES | open ok, pwrite 4 bytes, target shows BBBB |

Conclusion: with the agent's capability set, a write into a shared mapping of another user's process works through the memfd reached via `/proc/<pid>/fd/<n>`, on every kernel tested. `CAP_SYS_PTRACE` alone does not suffice.
