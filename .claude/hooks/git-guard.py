#!/usr/bin/env python3
"""PreToolUse hook for the squad subagents: no Git write operation and no GitHub write from a member.

Every `.claude/agents/squad-*.md` declares this hook in its front matter, so it runs only while a squad
member is active - the orchestrator (the session that runs the skill) commits, pushes and posts as before.
It reads the hook input JSON from stdin and, for a `Bash` call, checks every `git` and `gh` invocation in
the command (also behind `&&`, `;`, `|`, `$(…)` and `git -C <dir>`):

- allowed: read-only Git (`status`, `diff`, `log`, `show`, `ls-files`, `grep`, `rev-parse`, `merge-base`,
  `fetch`, `tag --list` / `--contains`, `branch --list` / `--show-current`, `ls-remote`, `clone`, …),
  `git add` (the coverage gate only counts tracked files) and `git worktree add` / `remove` / `prune` /
  `list` (scratch worktrees, `.squad/routing.md`, *Concurrency*), and read-only `gh` (`gh api` without a
  method or field, `gh pr view`, …);
- denied: `commit`, `push`, `stash`, `reset`, `checkout`, `switch`, `restore`, `rebase`, `merge`,
  `cherry-pick`, `revert`, `rm`, `mv`, `clean`, `pull`, `am`, `apply`, `tag <name>`, `branch -d/-D/-m/…`,
  `notes`, `update-ref`, `reflog`, `gc`, `filter-branch`, `config` (except `--get*`, `--list`), and `gh`
  writes (`gh pr create|comment|review|merge|close|edit|ready`, `gh issue create|comment|close|edit`,
  `gh api` with `-X`/`--method` other than GET, `-f`, `-F`, `--field`, `--raw-field`, `--input`).

A denied call answers `{"hookSpecificOutput": {"permissionDecision": "deny", …}}` with the reason, so the
member reports the need to the orchestrator instead. Anything else, including input the hook cannot
parse, is left to the normal permission flow (exit 0, no decision).
"""
import json
import re
import shlex
import sys

GIT_DENIED = {
    "commit", "push", "stash", "reset", "checkout", "switch", "restore", "rebase", "merge", "cherry-pick",
    "revert", "rm", "mv", "clean", "pull", "am", "apply", "notes", "update-ref", "reflog", "gc",
    "filter-branch", "replace", "submodule", "bisect",
}
GIT_OPTIONS_WITH_VALUE = {"-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path"}
WORKTREE_ALLOWED = {"add", "remove", "prune", "list", "lock", "unlock"}
GH_WRITE_SUBCOMMANDS = {
    "pr": {"create", "comment", "review", "merge", "close", "edit", "ready", "reopen", "lock", "unlock"},
    "issue": {"create", "comment", "close", "edit", "reopen", "delete", "transfer", "pin", "unpin", "lock", "unlock"},
    "release": {"create", "delete", "edit", "upload"},
    "repo": {"create", "delete", "edit", "fork", "rename", "archive", "unarchive", "sync"},
    "label": {"create", "delete", "edit", "clone"},
}
GH_API_WRITE_FLAGS = {"-f", "-F", "--field", "--raw-field", "--input"}
SEGMENT_SPLIT = re.compile(r"\s*(?:&&|\|\||[;|()`\n]|\$\()\s*")


def git_subcommand(tokens):
    """(subcommand, arguments) of a git invocation, skipping global options such as `-C <dir>`."""
    i = 0
    while i < len(tokens):
        token = tokens[i]
        if token in GIT_OPTIONS_WITH_VALUE:
            i += 2
        elif token.startswith("-"):
            i += 1
        else:
            return token, tokens[i + 1:]
    return None, []


def git_verdict(tokens):
    sub, args = git_subcommand(tokens)
    if sub is None:
        return None
    if sub in GIT_DENIED:
        return f"git {sub}"
    if sub == "tag" and args and not any(a in ("-l", "--list", "--contains", "--points-at", "--merged", "--no-merged")
                                         or a.startswith("--list=") for a in args):
        return "git tag <name>"
    if sub == "branch" and any(a in ("-d", "-D", "-m", "-M", "-c", "-C", "-f", "--delete", "--move", "--copy",
                                     "--force", "--set-upstream-to", "-u", "--unset-upstream", "--edit-description")
                               for a in args):
        return "git branch (delete/move/copy/upstream)"
    if sub == "branch" and args and not any(a in ("-l", "--list", "-a", "-r", "--all", "--remotes", "--show-current",
                                                  "--contains", "--merged", "--no-merged", "-v", "-vv", "--verbose")
                                            or a.startswith("--") for a in args):
        return "git branch <name>"
    if sub == "worktree" and (not args or args[0] not in WORKTREE_ALLOWED):
        return "git worktree " + (args[0] if args else "")
    if sub == "config" and not any(a.startswith("--get") or a in ("-l", "--list", "--show-origin", "--show-scope")
                                   for a in args):
        return "git config (write)"
    return None


def gh_verdict(tokens):
    if len(tokens) < 2:
        return None
    group, rest = tokens[0], tokens[1:]
    if group == "api":
        for i, a in enumerate(rest):
            if a in GH_API_WRITE_FLAGS or a.startswith(("--field=", "--raw-field=", "--input=")):
                return "gh api with a field or input (a write)"
            if a in ("-X", "--method") and i + 1 < len(rest) and rest[i + 1].upper() != "GET":
                return f"gh api {a} {rest[i + 1]}"
            if a.startswith("--method=") and a.split("=", 1)[1].upper() != "GET":
                return f"gh api {a}"
        return None
    writes = GH_WRITE_SUBCOMMANDS.get(group)
    if writes and rest[0] in writes:
        return f"gh {group} {rest[0]}"
    return None


def basename(token):
    return token.rsplit("/", 1)[-1]


def check_command(command):
    """The first denied invocation in the command as text, or None."""
    for segment in SEGMENT_SPLIT.split(command):
        try:
            tokens = shlex.split(segment)
        except ValueError:
            tokens = segment.split()
        for i, token in enumerate(tokens):
            name = basename(token)
            if name == "git":
                found = git_verdict(tokens[i + 1:])
            elif name == "gh":
                found = gh_verdict(tokens[i + 1:])
            else:
                continue
            if found:
                return found
            break
    return None


def main():
    try:
        data = json.load(sys.stdin)
    except ValueError:
        return 0
    if not isinstance(data, dict) or data.get("tool_name") != "Bash":
        return 0
    command = (data.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        return 0
    found = check_command(command)
    if found is None:
        return 0
    agent = data.get("agent_type") or "a squad member"
    reason = (f"{found} is a Git or GitHub write operation, which {agent} never runs: subagents report, the "
              "orchestrator commits, pushes and posts (.squad/team.md, Shared rules). Scratch worktrees "
              "(git worktree add/remove) and git add stay allowed.")
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                             "permissionDecisionReason": reason}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
