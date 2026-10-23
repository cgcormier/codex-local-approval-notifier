# Codex Approval Tray

A small native Windows tray app for Codex approval alerts. Uses the installed
.NET Framework and Codex lifecycle hooks; no extra runtime, web server, terminal
scraping, or transcript scanning.

## Use

- Left-click the tray icon to see alerts. Each session gets its own row, including
  its folder, full path, and a short session ID, even when folders are identical.
- Dismiss a pop-up with its **Dismiss** button. Pop-ups disappear after 9 seconds;
  the alert stays in the tray list. In the list, press **Delete** to dismiss the
  selected session or **Esc** to close the list. **Dismiss all** clears the list.
- Right-click for **Pause notifications**, **Start with Windows**, a test alert,
  and **Exit**. Pausing hides pop-ups while retaining alerts in the list.
- Approve or deny inside Codex. Dismissing an alert does not answer the request.

The icon turns amber for alerts and gray when paused. Session/turn-end and
interrupt hooks clear old notices. These are approval *notices*, not an exact
count of currently blocked commands: a notice may remain until the turn ends.
No commands, prompts, tool outputs, or approval descriptions are sent or saved.
Alerts are in memory only. Requests while the tray app is exited are discarded.

## Install and enable

Run `Install.ps1` in Windows PowerShell. It compiles with Windows' built-in C#
compiler, installs to `%LOCALAPPDATA%\Programs\CodexApprovalTray`, adds a Start
menu shortcut and per-user startup entry, and merges four hooks into
`~/.codex/hooks.json`. Existing hooks are preserved and timestamped backups are
made on change. The existing `notify` setting is untouched.

Restart each Codex CLI session, run **`/hooks`**, and review/trust the four
**Codex Approval Tray** handlers. Codex requires trust for each new hook
definition. The installer never changes trust records or approval policies.

All local Codex clients that load the user hooks can send notices. The shared
Codex app-server does not reliably expose the originating terminal HWND, so the
app labels sessions instead of trying to focus an arbitrary terminal window.
Sessions using another `CODEX_HOME`, remote hosts, disabled hooks, or an older
Codex version without `PermissionRequest` will not notify through this install.

## Build and check

```powershell
.\Build.ps1
.\Test.ps1
.\Test-Installed.ps1
```

Tests exercise separate sessions in the same directory, lifecycle races,
malformed input, IPC ACLs and delivery, dismissal, and fail-open
hook behavior. The installed-app check verifies four concurrent hook processes,
pause/resume, dismissal, and cleanup without disturbing existing alerts.
`--self-test <result-path>` renders the native UI for inspection;
`--test` sends a visible test pop-up to the running app.

To disable: exit the tray app and turn off **Start with Windows** first if
desired. To remove the integration, remove only the four groups whose handler
command ends in `CodexApprovalTray.exe --hook` from `~/.codex/hooks.json`, and
remove the **Codex Approval Tray** Start menu shortcut. Source and installed
files can then be removed manually. Keep other hooks and `notify` settings.

Official integration reference: https://learn.chatgpt.com/docs/hooks
