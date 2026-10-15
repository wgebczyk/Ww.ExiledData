# UI principles

Rules that hold across every screen. Values (colors, sizes) live in the code; see [README.md](README.md) for where.

## Context of use

- The app runs next to Path of Exile 2, often on a second monitor or in a window beside the game. Dark is the default theme; Light and System (follow Windows) are available in Settings, and the session list has a quick dark/light toggle. A theme switch applies to every open window at once, without a restart.
- The user's hands are on the game. Anything frequent while playing (scans, runs, undo, sweeps) has a global hotkey; the window shows the result and lets the user correct it afterwards. The app never sends input to the game.
- Information first, decoration never: flat surfaces, 1 px borders, no shadows or gradients in the app windows.

## Color

- Every color comes from a token in `Theme/Tokens.axaml`, referenced with `{DynamicResource ...}` so a theme switch re-resolves it. No hex values, `Color.Parse` or `Brushes.X` in UI code (a test checks the listed files).
- Tokens are named by role, not by hue value: `PanelRaised`, `AmberText`, `DangerLine`. Both themes define the same keys.
- Amber has three roles: `AmberFill` only for the primary button, `AmberText` for amber text, `AmberAccent` for dots, borders and accents. Blue likewise: `BlueFill` for bars and checkboxes, `BlueText` for text.
- Mode colors: Gather is amber, Analyze is blue. Amber also means "recording" (REC badge, Running pill, recording dot).
- Color is never the only signal: mode tabs keep their label, decreases carry ▼, warnings carry ⚠, a changed setting has a dot and a colored border.
- Contrast: text at least 4.5:1 against its background in both themes, indicators (bars, accents) at least 3:1. `TextFaint` is for disabled and placeholder text only. The pairs are asserted in `ThemeTokensTests`; add a pair there when you introduce a new text-on-background combination.
- The game overlays (capture indicator, sweep grid, grid editor) are not themed: they sit on arbitrary game imagery and use fixed high-contrast colors with a dark backdrop or outline.

## Type

- IBM Plex Sans for text, IBM Plex Mono for anything that is a number, code, count, time or duration, so columns of numbers align. Both are bundled.
- Section and table labels are small, letter-spaced capitals in the muted color. Titles are semibold, never bold.

## Layout

- Windows are made of cards on the window background, separated by a uniform gap. Title bars, footers and the status line use the chrome background.
- Tables live inside a card and scroll inside it; numbers are right-aligned.
- Each window has a sensible default size and a minimum size; cards flex, nothing is clipped without a scroll bar.
- One app window. Its app bar holds the way around: back (Alt+Left, the mouse back button, Escape on a session page), the app icon, then the breadcrumb Sessions / session / Run N, the session switcher (▾, Ctrl+K) and, on every other page, the recording session's REC pill, which opens it. Pages show the session list or one session; sessions are not shown side by side.
- A session page keeps its state (mode, picked run) while it stays alive: the recording session and the last few visited ones. The app bar is the only place Settings opens from.

## Interaction

- One primary (amber) button per area: the action the user most likely wants next.
- Destructive actions use the danger style and ask for confirmation. Edits of recorded data are undoable (Undo / Redo buttons and hotkeys) instead of asking first.
- Icon-only buttons always have a tooltip and an accessible name (`AutomationProperties.Name`).
- The Gather/Analyze mode tabs are the only place the mode switches.
- Changes to scope, filters, scan kinds or include checkboxes recompute the shown numbers immediately; there is no Apply button outside Settings.
- Settings is a draft: changes preview where it makes sense (theme), apply on Save, and Cancel puts everything back. Each changed field is marked.
- Every list has an empty state that says what to do to fill it.
- Only the recording session accepts captures; an ended session offers to become the recording one.

## Data shown

- Loot is what scans counted, not what was copied; the UI says where numbers come from ("from stash scans", "+ 1 adjustment").
- Analyze always shows its scope ("N of M runs used", or "A: 5 runs vs B: 4 runs") so a number is never read without knowing which runs it covers.
- Currency icons come from an icon folder that may be missing; every place that shows an icon has a fallback (the code's first letter on a tile).
