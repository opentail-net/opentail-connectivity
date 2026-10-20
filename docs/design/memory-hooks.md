# Memory hooks: what was checked, and how to set them up

**Status:** checked by a scratch test on 2026-10-19 with Claude Code 2.1.303 on Windows 11 (hooks run through Git Bash), Python
3.14, model Haiku, seven non-interactive sessions, about $0.015 in all. The memory was a stand-in: Markdown files with a header,
the shape of the planned files backend. Nothing here touches the Host; it checks only that client hooks give a memory what it
needs. Part of [plug-and-play memory](memory.md#who-writes); the hooks themselves are not built.

The test script is [memory-hooks-example.py](memory-hooks-example.py). It is a scratch script, not a product: it logs every
hook's input, injects an index, matches prompts by keyword and writes a session note.

## Result

| Question | Answer |
|---|---|
| Does a pushed index reach the model? | **Yes.** `SessionStart` context was used. Asked who owns Acme and where to pay, the model cited `memory://team/decision-acme-owner` as the fact, refused the account in the record labelled untrusted evidence, and gave the call-back rule. |
| Does recall at the prompt work? | **Yes.** `UserPromptSubmit` gets the prompt text and can add matching records. |
| Does capture get enough? | **Yes.** Every event gets `session_id`, `transcript_path` and `cwd`; `Stop` also gets `last_assistant_message`. |
| Can capture tell when a session read untrusted content? | **Yes, for client tools.** A session that used `WebFetch` produced a note marked `tainted: true`; one that used no tools did not. |
| Does the session survive a broken store? | **Yes.** A slow, crashing or missing store never stopped a session (exit 0, correct answer). But see the timeouts. |
| Does compaction trigger capture? | **Yes.** `PreCompact` fired (`trigger: manual`), then `SessionStart` fired again with `source: compact`, so the index is pushed again after compaction without extra work. |

## Settings to copy

Project scope (`.claude/settings.json`) or user scope. `$CLAUDE_PROJECT_DIR` is set for hooks.

```json
{
  "hooks": {
    "SessionStart":     [{ "hooks": [{ "type": "command", "command": "python \"$CLAUDE_PROJECT_DIR/hooks/memhook.py\" session-start", "timeout": 3 }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "python \"$CLAUDE_PROJECT_DIR/hooks/memhook.py\" prompt", "timeout": 3 }] }],
    "PreCompact":       [{ "hooks": [{ "type": "command", "command": "python \"$CLAUDE_PROJECT_DIR/hooks/memhook.py\" capture", "timeout": 10 }] }],
    "SessionEnd":       [{ "hooks": [{ "type": "command", "command": "python \"$CLAUDE_PROJECT_DIR/hooks/memhook.py\" capture", "timeout": 10 }] }]
  }
}
```

The test also hooked `Stop`; it is left out here (see [instructions](#instructions), 2). Timeouts are in seconds; the test used
5 and 10, the values above follow from what it found.

To add context, a hook prints one JSON object on stdout:

```json
{ "hookSpecificOutput": { "hookEventName": "SessionStart", "additionalContext": "Shared team memory ... index ..." } }
```

## What each event receives

Seen in the test (stdin JSON; values shortened):

| Event | Fields | When |
|---|---|---|
| `SessionStart` | `session_id`, `transcript_path`, `cwd`, `hook_event_name`, `source` | `source` was `startup`, `resume` and `compact` |
| `UserPromptSubmit` | the above (no `source`) plus `prompt_id`, `permission_mode`, `prompt` | Before each prompt reaches the model |
| `Stop` | plus `effort`, `stop_hook_active`, `last_assistant_message`, `background_tasks`, `session_crons` | **After every turn** |
| `PreCompact` | plus `trigger` (`manual` or `auto`), `custom_instructions` | Before compaction |
| `SessionEnd` | plus `reason` (`other` in `-p` mode) | Once, at the end |

Environment: `CLAUDE_PROJECT_DIR` and `CLAUDE_CODE_SESSION_ID` are set. The test sessions were started from another Claude Code
session, so `CLAUDE_CODE_CHILD_SESSION` was also set; a standalone session may differ.

## Instructions

For whoever writes the real hooks:

1. **Keep the blocking hooks fast.** `SessionStart` and `UserPromptSubmit` block the session. With a store that never answered,
   a one-word reply took 36 s instead of 4 s: every hook waited for its full timeout. Use timeouts of 2-3 s for these two,
   answer from a local cache of the index, and refresh the cache outside the prompt path.
2. **Capture on `SessionEnd` and `PreCompact`, not `Stop`.** `Stop` fires after every turn, so a session of twenty turns writes
   twenty notes; in the test every session wrote a `Stop` and a `SessionEnd` note with the same content. If `Stop` is needed (it
   is the only event with `last_assistant_message`), write keyed by `session_id` so each turn replaces the last note.
3. **Never create the store.** With the store path missing, the test's capture hook created a new folder and wrote there, and
   nobody noticed. A writer must fail when the store or the Host is not there, and report it.
4. **Report failures somewhere a person looks.** In `-p` mode only the `SessionEnd` failure showed on stderr; the others were
   silent. Hooks should log their own failures (and the Host should count writes per hook identity, so a hook that stops writing
   shows up).
5. **Untrusted marking: decide it in the Host for MCP tools, in the hook for client tools.** The hook can see client tool names
   in the transcript (`WebFetch`, `WebSearch`; `ToolSearch` also appears), but it cannot know which MCP tools read untrusted
   content. The Host already marks that on its own calls, so a note about a session that used Host tools should get its marking
   from the Host's audit, not from the hook's guess.
6. **Prefer payload fields to the transcript.** The transcript is JSONL with tool calls as `message.content[]` parts of
   `type: tool_use`. It is Claude Code's internal format and may change. Use `prompt` and `last_assistant_message` where they
   are enough.
7. **The hook writes under its own identity.** The test hard-coded `hook:claude-code-scratch`. A real hook takes an API key from
   the environment, and the Host decides trust from that identity (see [who writes](memory.md#who-writes)).
8. **Say which memory it is.** Claude Code has its own memory (settings `autoMemoryEnabled`, `autoMemoryDirectory`,
   `autoDreamEnabled`). The pushed text must say "shared team memory (what colleagues and systems know; not your own memory)",
   as the test did, or the model mixes the two.
9. **The note itself needs a model.** The test wrote only the last request. Turning a session into decisions and corrections
   needs a model call (in the hook, or done by the Host on receipt), which is not tested here.
10. **Testing from Git Bash:** `claude -p "/compact"` was rewritten by Git Bash to `C:/Program Files/Git/compact`. Set
    `MSYS_NO_PATHCONV=1` when passing slash commands.

## Not tested yet

- **Hook types that need no script.** Claude Code hooks can be `"type": "http"` (POST the hook input to a URL, with headers from
  allow-listed environment variables) or `"type": "mcp_tool"` (call a tool on a configured MCP server, with `${path}` values
  from the hook input). Either could send capture straight to the Host's process path or `memory.write`, with no script at all.
  Worth checking first when M9 starts.
- `"async": true` for capture hooks, which should take them off the session's critical path.
- Automatic compaction (`trigger: auto`) and interactive sessions; the test used `-p` and a manual `/compact` only.
- Other clients' hooks (none checked).
- Volume over many sessions, and duplicate detection on real notes.
