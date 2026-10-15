# Components and screens

What exists and what it is for. Sizes and colors are in the styles; look at `screens/` for how they render. Keep this list to names and purposes so it stays true.

## Style classes

Opt in by class; a plain control keeps the Fluent look. Files are under `src/Ww.ExiledData.App/Theme/`.

### Shared (`Controls.axaml`)

| Class | Use |
|---|---|
| `Window.app` | every app window: background, font, base size, icon |
| `Border.chrome` | title bars, footers, the status line |
| `Border.card`, `Border.card.flush` | a card; `flush` for a card holding a table (no padding, clipped) |
| `Border.raised` | an inner block inside a card (waystone, scan summary, banners) |
| `TextBlock.secondary` / `.muted` / `.faint` / `.amber` / `.error` | text roles |
| `TextBlock.small`, `.mono`, `.label`, `.title` | small text, numbers, section/table labels (write them in capitals), window and dialog titles |
| `Border.chip` | a tag; with `Button.chipx` inside for removal |
| `Border.rec`, `Border.pill`, `Ellipse.recdot` | REC badge, Running pill, recording dot |
| `Button.ui` | base button; variants `primary`, `outline`, `danger`, `filter`, `link` |
| `Button.crumb` (+ `current`), `Button.recpill` | a breadcrumb segment in the app bar (the page shown is `current`); the recording session's pill there |
| `Button.addchip` | dashed "add" affordance (+ tag, + Add filter) |
| `Border.segmented` + `RadioButton.segment` | segmented toggle (Grid/List, Run/Session, …) |
| `TextBox.search`, `ComboBox.filter` | toolbar inputs |
| `ListBox.table` + `Grid.tablehead` | selectable table with a header row |
| `DataGrid.ui` | read-only, sortable data inside a card |

### Mapping (`MappingControls.axaml`)

| Class / control | Use |
|---|---|
| `Icon*` geometries | the line icon set (settings, rename, export, delete, stop, play, recall, swipe, final, undo, redo, warning, close, back, chevron down), drawn with `StrokeIcon` |
| `RadioButton.modetab` + `gather` / `analyze` | the session page's mode tabs |
| `Button.ui.icon` (+ `bare`, `small`, `square`) | icon-only button; always with tooltip and accessible name |
| `Button.ui.icon.stop` / `.play` | the recording toggle: end the session / make it the recording one |
| `Border.filterchip` (+ `neutral`) | Analyze A vs B: the filters of A; the first / second half chips when there is none |
| `Border.groupA` / `groupB` | A filled blue, B outlined orange: run markers and the A = / B = labels |
| `Border.splitcount` (+ `small` / `empty`) | "A: 5 runs vs B: 4 runs"; amber for small samples, red when a side is empty |
| `TextBlock.blue`, `.up`, `.down`, `.warning` | blue text, increases, decreases, warnings |
| `ProgressBar.statbar` | the bar in the currency statistics |
| `CheckBox.use` | use a run or a currency in the Analyze values |
| `Button.kindchip` (+ `stash`, `final`) | a scan's kind; opens the change menu |
| `Panel.slot` | a tablet slot (filled or dashed empty) |
| `Border.badge` | L / G / P tier next to a currency code |
| `Image.currencyIcon` | a currency icon in lists |

### Settings (`SettingsControls.axaml`)

| Class | Use |
|---|---|
| `ListBox.nav`, `TextBlock.navlabel` (+ `soon`), `Border.soonchip`, `Border.errorpill` | the category list |
| `TextBlock.rowlabel`, `.desc`, `.helper`, `.danger` | a setting row's label, description, helper and error lines |
| `Ellipse.dot` | "changed, not saved" marker |
| `Border.field`, `Border.stepper`, `Button.step` (+ `small`) | text field and number stepper, with `changed` / `error` states |
| `Border.recorder`, `Border.keycap` | hotkey recorder and its key caps (`changed`, `error`, `warning`, `recording`) |
| `RadioButton.option` | a radio option in a list |
| `Button.inline` (+ `danger`), `Button.dashed` | small text actions (Reset to …), "+ Add area" |
| `Border.banner`, `Border.info`, `Border.sep` | warning banner, info box, row separator |
| `Button.lootslot` (+ `empty`), `Button.lootmatch` | loot grid editor slot and picker row |

## Custom controls (`src/Ww.ExiledData.App/Controls/`)

| Control | Use |
|---|---|
| `CurrencyCell` | one cell of the loot grid: icon tile (letter fallback), tier badge, code, count; states zero, changed, placeholder |
| `CurrencyIcons` | loads icons from the data folder's icon directory; missing files fall back |
| `AnalyzeChart` | LiveCharts2: bars per run with the running total on a right axis; A vs B bars (A filled, B outlined) and running totals (A solid, B dashed) |
| `StrokeIcon` | draws an `Icon*` geometry in the current foreground |
| `KeyRecorderBox` | records a hotkey |
| `GridPreview` | small proportional drawing of a sweep area |
| `PlacementPreview`, `TimingBar` | the feedback overlay's placement and timing previews |

## Screens

Screenshot names are the files in `screens/`, each as `-dark.png` and `-light.png`.

| Screen | View | Screenshots |
|---|---|---|
| Main window: app bar (back, app icon, breadcrumb, switcher, REC pill, settings) | `Views/MainWindow.axaml` | on every page screenshot |
| Session list | `Views/SessionListView.axaml` | `session-list` (empty), `session-list-filters` (with sessions, the date range and the REC pill) |
| Session page, free recording | `Views/SessionView.axaml`, `Views/CapturesView.axaml` | `recording` |
| Session page, Gather | `Views/GatherView.axaml` | `gather`, `gather-list`, `gather-items`, `gather-review` (a picked run: the breadcrumb ends in Run 2) |
| Session page, Analyze | `Views/AnalyzeView.axaml` | `analyze`, `analyze-compare` |
| Recall dialog | `Views/RecallDialog.axaml` | `recall`, `recall-tablets` |
| Review dialog | `Views/ReviewDialog.axaml` | `review` |
| New session, prompt, confirm | `Views/NewSessionDialog.cs`, `PromptDialog.cs`, `ConfirmDialog.cs` | `new-session`, `prompt`, `confirm` |
| Settings | `Views/SettingsWindow.axaml` | `settings-<page>` for every page; `settings-<page>-states` for edited, recording, duplicate and error states |
| Capture indicator | `Overlay/CaptureIndicator.cs`, sheet in `Overlay/IndicatorSheet.cs` | `capture-indicator` (one picture: the overlay is not themed); `--overlay-demo` animates them over the screen |
| Sweep grid and grid editor | `Overlay/GridPainter.cs`, `GridChrome.cs`, `GridEditorWindow.cs`, `GridOverlayWindow.cs`; sheet in `Overlay/GridChromeSheet.cs` | `sweep-grid` (positioning, ready, game not in front) |
