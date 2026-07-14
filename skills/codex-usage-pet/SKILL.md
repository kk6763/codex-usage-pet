---
name: codex-usage-pet
description: Control the Codex Usage Pet, a tiny Windows Tail Meter focused on real remaining Codex allowance. Its badge and seven tail dots represent the current 5-hour window's remaining percentage, while details include 5-hour and weekly remaining allowance, reset countdowns, and token activity. Use when the user asks to check remaining allowance, start, show, wake, hide, refresh, close, or quit the pet, or to enable or disable opening it automatically with Codex, including Chinese requests such as 查看剩余额度、启动用量宠物、显示额度宠物、刷新用量、隐藏宠物、关闭宠物、打开 Codex 时自动启动、取消自动启动。
---

# Codex Usage Pet

Run the matching bundled command from this skill directory:

- Start or show: `scripts\show.cmd`
- Hide: `scripts\hide.cmd`
- Refresh now: `scripts\refresh.cmd`
- Enable opening automatically with Codex: `scripts\autostart-enable.cmd`
- Disable opening automatically with Codex: `scripts\autostart-disable.cmd`
- Quit: `scripts\quit.cmd`

Use the shell directly and keep the command scoped to the selected action. Treat an unspecified action as start/show. Only change the automatic-start setting when the user explicitly asks for that change.

The automatic-start command registers a tiny local watcher for the current Windows user. It opens the pet when the Codex desktop app appears and closes the pet after Codex exits. Disabling it removes only that watcher registration; it does not erase the saved pet position or size.

After starting, tell the user that:

- the badge and seven tail dots show the current 5-hour window's **remaining allowance percentage**;
- clicking the pet opens 5-hour and weekly remaining allowance, reset countdown, and token activity details;
- dragging moves it, right-clicking opens its local menu, and Ctrl+mouse-wheel adjusts its size;
- its last position and size are restored the next time it opens.

Never invent usage values. The executable first uses the authenticated local Codex app-server and falls back to numeric `token_count` fields in local session JSONL. It must not read, copy, print, or upload `auth.json`, prompts, responses, file contents, or tool output.

If a bundled command fails, report the error and the attempted action; do not install unrelated runtimes.
