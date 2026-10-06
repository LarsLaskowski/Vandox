# Spec: Import framework for log archives

Status: Draft
Source: Issue #15

## Problem / motivation

Release v0.1.0 is the forensics release (0020): Vandox has to explain past outages (memory exhaustion,
OOM killer, MariaDB, Plesk and mail down) from logs the maintainer saved on the backend host — `/var/log`
with rotated `.gz` files, a journal export (`journalctl -o export`) and the legacy `top`/`lsof` log. Log
import is a core component (0014), but nothing reads such an archive yet: `vandoxd` only runs the service
or its health probe, and every positional argument is a usage error (`cmd/vandoxd/main.go`, 0058).

This feature builds the **framework**: reading a directory or archive safely and streaming, detecting the
source type per file, handing each file to the matching parser, storing the records idempotently, and
reporting progress and a summary. The parsers themselves are the follow-up issues #16 (journal export,
syslog, kern.log), #17 (MariaDB), #18 (mail.log), #19 (Plesk, web server) and #20 (legacy log). Until they
land, `vandoxd import` recognizes no file and lists every file as not recognized.

## Behavior

- `vandoxd import <path>` (also `vandoxd [-config file] import [-config file] <path>`) imports the
  directory, archive or file at `<path>` into the database named by the configuration
  (`storage.directory`). In the container the compose file already mounts `./import` read-only at
  `/import` (0060), so the operator runs `docker exec vandoxd /vandoxd import /import/<name>`.
- **Inputs:** a directory (read recursively), a `.tar`, a gzip-compressed tar (`.tar.gz`, `.tgz`), a
  single gzip file (a rotated log), or a plain file. Compression and archives are recognized by their
  content, not by the file name. Gzip-compressed files inside a directory or a tar archive are
  decompressed; a tar archive found in the directory is read as well. An archive inside an archive is not
  opened. Nothing is ever extracted to disk.
- **Detection:** for every file, each registered parser rates the file's name and its first 4 KiB; the best
  match parses the file. A file no parser claims is **listed** as not recognized, with the reason (no
  parser, empty, unsupported compression such as bzip2/xz/zstd/zip, nested archive, symbolic link not
  followed, not a regular file). Nothing is skipped silently.
- **Streaming:** files are read as streams; memory use does not grow with the file size. Lines longer
  than 16 KiB are cut (and marked truncated) by the shared line reader.
- **Idempotent:** the SHA-256 of every imported file's (decompressed) content is stored. A file whose
  content was imported completely before is not imported again — also under another name or compressed
  differently (`syslog.1` and the later `syslog.2.gz`). An import that was interrupted (Ctrl-C, container
  stop, a database error) continues where it stopped when the same input is imported again; no record is
  stored twice.
- **Progress** is logged as JSON lines on standard error (like the service's log): what was found, each
  file started and finished, and progress every 100,000 lines of a large file.
- **Summary** on standard output at the end: number of files found, imported, already imported, not
  recognized and failed; lines read, records stored and lines skipped; the time range of the stored
  records; the list of files not recognized and of files that failed, each with its reason; and lines a
  parser skipped, with the first reasons per file. Names from the input are printed quoted, so a control
  character in a file name cannot forge output lines.
- **Exit code:** 0 when every file was imported, already imported or not recognized; 1 when a file
  failed, the import was interrupted, or the configuration, the database or the input could not be
  opened; 2 for a usage error.
- **Failures** of one file (a corrupt or truncated gzip file, a parser error, a file that changed while it
  was read) are reported in the summary and the import continues with the next file. A file that cannot
  be read completely is not imported at all. A database error stops the import; running it again
  continues it.
- The service and an import may run at the same time; they share the database's single-writer lock in
  short batches.

## Acceptance criteria

- [ ] AC1: Re-importing the same archive creates no duplicates — also after an interrupted import, and for
  the same content under another name or compression.
- [ ] AC2: Unrecognized files are listed with a reason, not silently skipped.
- [ ] AC3: The summary shows files, lines, time range and errors.
- [ ] AC4: Memory usage stays flat for large files (tested with a generated large file).
- [ ] AC5: `vandoxd import <path>` is the CLI trigger; the parser interface and registry exist so that
  #16–#20 only add parsers.

## Out of scope

- The parsers of #16–#20; the web UI trigger (a later issue); the views of the imported data.
- Importing only the new tail of a log file that grew since it was imported (e.g. `syslog` imported
  today, the same lines again as part of `syslog.1` next week): such a file has another content hash and is
  imported as a whole. Follow-up issue proposed (plan, *Out of scope / follow-ups*).
- Partially importing a truncated or corrupt compressed file; compressions other than gzip; archives
  other than tar; archives nested in archives.
- Deleting or re-doing an import.

## Open questions

None for the Product Manager. The user-visible details the issue leaves open (command form, output
streams, exit codes, what is listed and why, limits) are decided in the decision records 0069–0072.
