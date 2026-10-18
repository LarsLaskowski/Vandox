# Real socket table samples (feature #33)

Two captures of the kernel's socket tables, both real kernel output. The plan (`plan.md`, *Test data*) says where the Tester
copies them (`testdata/proc/net/` and `testdata/connections/sandbox/`); this folder leaves the branch with the working record.

## `server/` — the monitored server (Ubuntu 22.04, Plesk 18)

`tcp`, `tcp6`, `udp`, `udp6`: `/proc/net/{tcp,tcp6,udp,udp6}` captured by the Product Manager on the monitored server on 2026-10-18
(uploaded to the session, not committed raw). Variants they hold:

- `tcp` (28 sockets): LISTEN (`0A`) on `0.0.0.0`, loopback, `127.0.0.53` and the public address; two LISTEN sockets on the same
  endpoint (`127.0.0.1:53`, `127.0.0.1:953`, public `:53`: BIND's sockets per endpoint); an ESTABLISHED `sshd` connection with a
  non-zero send queue, a running timer and `-1` as its last field; a TIME_WAIT line with inode 0 and fewer trailing fields; the header
  and every line padded with trailing spaces to the kernel's fixed width; uids 0, 102, 114, 115, 116, 118, 998.
- `tcp6` (25 sockets): LISTEN on `::`, `::1`, the public IPv6 address and a link-local address (`fe80::`); two sockets per endpoint for
  port 53; no trailing spaces, header without padding. **Not covered:** an ESTABLISHED IPv6 connection and an IPv4-mapped address
  (`::ffff:a.b.c.d`); tests derive such lines from a real line by replacing only the address field (plan, *Verified facts*).
- `udp` (7 sockets): connected sockets (`01`) of the resolver to a provider DNS server; unconnected sockets (`07`) on the public
  address, loopback and `127.0.0.53`; four lines with the **same `sl` value** (`721`, a hash bucket, so `sl` is not an identity); trailing
  spaces.
- `udp6` (6 sockets): unconnected sockets on `::1`, the public IPv6 address and a link-local address, `sl` repeated; no trailing spaces.

**Anonymized** (by the Lead, with a script that replaces each whole address field by a documentation value of the same width that takes
nothing from the real value — only the generic `fe80::/64` prefix of the link-local address is the same — checked to keep the line count
and the byte length of every line, and checked by listing every changed address field against its original):

| Captured | Replaced by | Role |
| -------- | ----------- | ---- |
| the server's public IPv4 address | `192.0.2.10` (`0A0200C0`) | local address |
| the `ssh` client | `198.51.100.7` (`076433C6`) | remote address |
| the peer of the TIME_WAIT socket | `198.51.100.20` (`146433C6`) | remote address |
| the provider's resolver (a private address) | `192.0.2.53` (`350200C0`) | remote address |
| the server's public IPv6 address | `2001:db8::10` (`B80D0120000000000000000010000000`) | local address |
| the server's link-local address (its interface identifier comes from the MAC) | `fe80::200:5eff:fe00:5301` (`000080FE00000000FF5E0002015300FE`; the interface identifier of the documentation MAC range) | local address |
| the kernel pointer column (`ffff…`, the root view, a KASLR leak) | `00000000` + 8 **random** hex digits (`os.urandom`, not derived from the real pointer; the shape an unprivileged reader sees) | ignored field |

Unchanged: `127.0.0.1`, `127.0.0.53`, `::`, `::1`, `0.0.0.0`, every port, state, queue, timer, uid, inode, `sl` value, the header lines,
the spacing and the trailing spaces. Addresses are written as the kernel writes them: each 32-bit word of the address in host byte order
(little-endian) as 8 upper-case hex digits; the port as 4 hex digits of its value.

## `sandbox/` — the squad sandbox (kernel 6.18.44, no IPv6)

Captured by the Lead on 2026-10-18 with a scratch script that held known sockets open while it copied the tables byte for byte (run as
root; the pointer column is already hashed there):

- `tcp`: LISTEN on `0.0.0.0:4444` (inode 16082) and on loopback, an ESTABLISHED pair `127.0.0.1:4444` <-> `127.0.0.1:55388` (inodes
  16084, 16083), one TIME_WAIT line with inode 0, and outbound ESTABLISHED connections of the sandbox (`192.0.2.2`) to port 443, whose
  remote addresses were replaced by `198.51.100.100` to `198.51.100.102` in the same way.
- `udp`: an unconnected socket on `0.0.0.0:5353` (state `07`, inode 16080) and a connected one `127.0.0.1:36728` -> `127.0.0.1:53`
  (state `01`, inode 16081).
- `fd-of-capturing-process.txt`: the capturing process's `/proc/self/fd` links: `socket:[16080]` to `socket:[16084]` match the table
  lines above, and `socket:[16078]` (standard input) is a socket in none of the four tables. Not copied to `testdata/`: the tests build
  these links as symbolic links in an `fstest.MapFS`.
- `fdinfo/`: `/proc/self/fdinfo/<n>` of the same kind of process, captured on 2026-10-18 (revision after the security review), one file
  per descriptor type: `tcp-listen`, `udp`, `unix` (sockets: `mnt_id: 10`, the internal sockfs mount; `ino:` equals the table's inode,
  6087 for the TCP listener), `pipe` (`mnt_id: 16`, pipefs; inode 6090, from the same counter as the sockets, so a pipe never shares a
  socket's number), `regular-file` (`mnt_id: 28`, a mount that `/proc/self/mountinfo` lists; inode 210788 from the file system) and
  `epoll-40-watches` (3,122 bytes: the four common lines, then 40 `tfd:` lines that hold `ino:` again, in hex). The common lines `pos:`,
  `flags:`, `mnt_id:`, `ino:` come first in every file; mount IDs 10 and 16 are not in the capturing process's `mountinfo`, 28 is.
