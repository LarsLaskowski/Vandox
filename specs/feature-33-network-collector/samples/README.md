# Real socket table samples (feature #33)

Captured by the Lead on 2026-10-18 in the squad sandbox (kernel 6.18.44, no IPv6: `tcp6` and `udp6` do not exist there)
with a scratch script that held known sockets open while it copied the tables byte for byte:

- `tcp`: `/proc/net/tcp` with LISTEN (`0A`) on `0.0.0.0:4444` (inode 16082) and on loopback, an ESTABLISHED (`01`) pair
  `127.0.0.1:4444` <-> `127.0.0.1:55388` (inodes 16084, 16083), one TIME_WAIT (`06`) line with inode 0, and outbound
  ESTABLISHED connections of the sandbox to port 443; lines padded with trailing spaces as the kernel writes them.
- `udp`: `/proc/net/udp` with an unconnected socket on `0.0.0.0:5353` (state `07`, inode 16080) and a connected one
  `127.0.0.1:36728` -> `127.0.0.1:53` (state `01`, inode 16081); header with `ref pointer drops`.
- `fd-of-capturing-process.txt`: the capturing process's `/proc/self/fd` links; `socket:[16078]` (standard input) is a
  socket that is in none of the four tables.

The Tester copies them to `testdata/proc/net/` (plan). They leave the branch with the working folder.
