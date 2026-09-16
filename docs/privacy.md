# FoxMouse privacy notice

FoxMouse v0.1 operates locally and does not contain product analytics,
advertising, account, cloud-sync or telemetry clients.

FoxMouse processes relative pointer movement, cursor identity, display geometry
and foreground-process identity in memory only to detect the shake gesture and
apply exclusions. It does not record clicks, keys, screen contents, window
titles or file paths. When the user opens the excluded-application picker, it
temporarily enumerates readable application names, executable names, paths, and
visible-window titles for the current login session so the list can be searched.
Only an explicitly selected, normalized executable basename such as `game.exe`
is stored; titles, paths, PIDs, and search text are neither persisted nor logged.

Diagnostic logs are local, bounded and contain lifecycle events, effect mode,
failure class and coarse configuration values. They do not contain raw pointer
coordinates. A motion trace is written only when the user explicitly starts the
`--record-trace` command; it contains normalized relative deltas and timestamps
and is never uploaded by FoxMouse.

Settings and logs are stored below `%LOCALAPPDATA%\FoxMouse`. Uninstall can
remove them, or the user may retain settings explicitly.
