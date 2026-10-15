# Currency catalog

Every stackable currency-like item of poe2db (currency, augments, omens, liquid emotions, essences, catalysts):
in-game name, short code, category, poe2db page, icon path and max stack size. About 775 items.

| File | |
|---|---|
| `catalog.json` | The catalog. Committed; codes in it are kept by every update, so edit them freely. |
| `overrides.json` | Preferred codes (name -> code), applied first on every update. |
| `update-catalog.ps1` / `.sh` | Re-reads poe2db and refreshes the catalog. |
| `fetch-icons.ps1` / `.sh` | Downloads the icons into the local data folder. |

The app embeds `catalog.json` (`Ww.ExiledData.Mapping`, `CurrencyCatalog`) for codes and icon paths, so rebuild after
an update.

PowerShell and bash versions do the same thing and produce byte-identical output. The bash versions need `curl`
and `perl` (Git Bash has both).

## Icons

The icons are game art from poe2db's CDN, so they are not in the repo. Fetch them locally:

```
pwsh tools/currency-catalog/fetch-icons.ps1      # or: tools/currency-catalog/fetch-icons.sh
```

They land in `%LocalAppData%\Ww.ExiledData\icons` (or `$WW_EXILEDDATA_HOME/icons`; `-Out` / `--out` to choose)
under poe2db's own paths, so an item's `image` in the catalog is its path below `icons`. Items that share art share
one file (about 530 files). Files already there are skipped, so a re-run only fetches what is new.

## Codes

A code is 2-4 letters, unique across all categories ignoring case, plus the category's prefix letter:
none for Currency and Emotions, `a` Augment, `o` Omen, `e` Essence, `c` Catalysts. An item listed on two pages (a
liquid emotion is on the Currency page too) has the same code apart from the prefix.

- Initials of the name without filler words (`Orb of Alchemy` -> `OA`); on a collision one or two words get more
  letters (`Orb of Augmentation` -> `OAu`), then a middle word is dropped, then letters are picked from the name.
- A number stays as digits (`... (Level 14)`).
- `Lesser` / `Greater` / `Perfect X` get X's code plus `l` / `g` / `p` (`COg`). These are reserved together with X's
  code.
- Markers (`overrides.json` -> `markers`): a phrase in the name is dropped and its letters lead the code, the rest
  gets initials as usual. `Liquid` -> `l`, `Concentrated Liquid` -> `cl` (longest phrase wins): `Liquid Paranoia` ->
  `lP`, `Potent Liquid Melancholy` -> `lPM`, `Concentrated Liquid Fear` -> `clF`.
- Overrides win and pass on to the tiers (`Exalted Orb: EX` -> `EXg`, `EXp`). An override that takes another
  item's existing code stops the update; change one of them.

## Updating

```
pwsh tools/currency-catalog/update-catalog.ps1   # or: tools/currency-catalog/update-catalog.sh
git diff tools/currency-catalog/catalog.json
```

Existing items keep their code; names, icons and stack sizes are refreshed. To regenerate a code (e.g. after
changing the rules), set it to `""` in `catalog.json` and run the update. New items get a generated code (printed
as `new: ...`). Items no longer on poe2db stay in the catalog (old stashes still hold them) and are printed as
`kept`. Two existing codes differing only in case are reported as a warning and left alone.
