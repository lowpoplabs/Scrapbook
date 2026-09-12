# Changelog

## [0.5.1] — 2026-09-12

First public release on GitHub — no gameplay changes.

### Added
- One-line load message naming the author and tip jar (`Scrapbook v0.5.1 loaded - by LowPopLabs - ko-fi.com/lowpoplabs`).
- README Support section with the Ko-fi button; support posture is as-is, issues welcome.

### Changed
- Project notes (`CLAUDE.md`) no longer reference the private test server.

## [0.5.0] — 2026-08-16

uMod-readiness pass — no gameplay changes.

### Added
- `scrapbook.admin` permission: `sb.wipe` and `sb.gc` now accept it as an alternative to auth level 2 (server console always allowed).
- MIT license (`LICENSE`) and uMod submission page copy (`docs/uMod.md`).
- New lang keys for the previously hardcoded admin replies: `AdminWipeUsage`, `GcAborted`, `GcCooldown`, `GcDone`, `GcDryRun`.

### Changed
- `UsePermission` now defaults to `"scrapbook.use"` on fresh installs (uMod guideline: features gated by permission). Existing configs are untouched; set it to `""` for the old everyone-can-use behavior.
- `sb.gc` has a 60-second cooldown — the full-disk book scan is the plugin's one expensive operation, and admin features must be abuse-resistant per uMod performance guidelines.
- Admin console replies are localized per-caller through the Lang API (in-game admins get their language; server console gets English).
- Source comments no longer reference companion-plugin internals.

## [0.4.0] — 2026-08-12

### Added
- **Papers section**: written `note` items (RustQuests FIELD NOTES, lore notes, player-written paper) can now be stored alongside photo prints. New Prints / Papers tabs in the book header, each with its own page position; the single **Store pages** button absorbs both kinds from main + belt in one press.
- Paper pages render as text on the matte — title, rule, and body word-wrapped by hand against a conservative line budget (18 lines ≈ FontSize 14), ending with an ellipsis when a note is longer than the page. Rich-text markup in player-written notes is neutralized for display only.
- Paper entries live inline in the player's book file (`{Hash, Title, Text, AddedAtUtc}`, hash = SHA-1 of the UTF-8 text) — no pooled image file, so `sb.gc` is unaffected. Duplicate detection and `AbsorbDuplicates` work exactly as for prints, keyed on the text hash.
- **Take a copy** on a paper mints a fresh `note` item with the identical stored text (and title, if the original was named). **Remove** keeps its two-click confirm.
- `MaxPrintsPerPlayer` is now one shared cap: photos + papers count against it together. `sb.wipe` clears both sections.

### Changed
- Store button label is now `Store pages (N)` (new lang key `UiStoreAll`; the old `UiStore` key is retired). Empty-book text is per-section.

## [0.3.0] — 2026-08-10

### Added
- Admin `sb.gc` (authLevel 2 / server console): garbage-collects pool images no book references. Dry-run by default with a count/size report; `sb.gc run` deletes. Scans every player book on disk — not just online players — and aborts without deleting anything if any book file is unreadable. Deleted images also have their per-boot CUI FileStorage cache entries removed.

## [0.2.0] — 2026-08-10

### Added
- **Remove** button in the book footer: deletes the current page from the scrapbook. Two-click confirm — first click arms the button ("Really?"), any other action (paging, storing, taking, closing) disarms it.
- Removed entries leave the shared image pool untouched: other players' books may reference the same print, and re-storing it later still dedupes against nothing (it's a fresh add).

## [0.1.0] — 2026-08-10

### Added
- Initial Scrapbook plugin: per-player CUI photo book at `/scrapbook` (`/sb`, console `sb.open`) that stores pamphlet and camera-photo prints without using inventory slots.
- **Store prints** absorbs every `photo` item with image data from main + belt; images are copied out of FileStorage into `oxide/data/Scrapbook/images/<sha1>.jpg` before the item is consumed, so the book survives item destruction, restarts, and wipes.
- Duplicate detection by SHA-1 of the JPG bytes; duplicates stay in the inventory by default (`AbsorbDuplicates` config to consume them instead).
- **Take a copy** mints a fresh photo item with the original image and photographer stamp — the page stays in the book; copies work in photo frames and can be given away.
- Page-through UI: fixed 900×590 book, true 16:9 print area with white matte, prev/next, page counter, per-print caption (title + date added).
- Config: chat command names, optional use-permission, per-player print cap (200), max image size (1 MB), duplicate policy.
- Admin `sb.wipe <name|steamid>`; Lang API for all player-facing text.
