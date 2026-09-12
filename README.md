# Scrapbook

*"Keep every print, spend no slots."*

A pocket scrapbook for low-pop Rust servers: a per-player CUI book that stores photo prints **and written paper notes** without using a single inventory slot. Pamphlets fished out of crates, camera photos, numbered field notes, letters signed with one initial — pasted into a book that survives item destruction, server restarts, and map wipes. Built as a companion to RustQuests and FakeFriends, whose printed handouts and lore papers are exactly what the book is for — but any vanilla `photo` or written `note` item works.

**Status: v0.5.1** (first public release; uMod-readiness pass in 0.5.0: permissions, localized admin replies, MIT license). Core loop (store / page / take a copy) and Remove are playtested live; the Papers section (v0.4.0) and `sb.gc` are deployed but not yet exercised in anger.

<!-- lpl:links -->
**[Download v0.5.1](https://github.com/lowpoplabs/Scrapbook/releases/latest)** · **Flyer:** [web](https://lowpoplabs.github.io/flyers/Scrapbook.html) / [PDF](Scrapbook-Flyer.pdf) · **[Changelog](CHANGELOG.md)** · **[Ko-fi](https://ko-fi.com/lowpoplabs)**
<!-- /lpl:links -->

## What it does

- **Prints** — any vanilla `photo` item with an image: camera shots, plugin-made pamphlets, gifted photos. The JPG is copied out of the game's FileStorage into the plugin's own pool before the item is consumed, so the picture outlives the paper.
- **Papers** — any vanilla `note` item with writing on it. The text is stored inline in the player's book file and renders as a readable page (title + wrapped body). A note longer than the page ends in an ellipsis rather than overflowing.
- **Two sections, one book** — Prints and Papers tabs in the book header, each with its own page position. One **Store pages** button sweeps every print and note from main + belt in a single press.
- **Take a copy** — any page mints back a real item (photo with original photographer stamp and image; note with byte-identical text and title). The book always keeps the original; re-storing a copy is detected as a duplicate.
- **Duplicate detection** — prints by SHA-1 of the image bytes, papers by SHA-1 of the text. Duplicates stay in your inventory with a message (or are consumed anyway with `AbsorbDuplicates: true`).
- **Remove, carefully** — two-click confirm: first click arms the button ("Really?"), any other action disarms it.
- **Wipe-proof** — books are plain data files, deliberately unhooked from map wipes. A scrapbook is a keepsake; nothing in the plugin resets it.

## Install

Drop `Scrapbook.cs` into `oxide/plugins/`. It writes a default config on first load. No dependencies.

## Commands

| Command | Who | Purpose |
|---|---|---|
| `/scrapbook`, `/sb` (chat) | players | Open the book (names configurable) |
| `sb.open` (console) | players | Same, bindable to a key |
| `sb.wipe <name\|steamid>` | admin (authlevel 2 or `scrapbook.admin`) | Clear an online/sleeping player's book, both sections |
| `sb.gc` / `sb.gc run` | admin (authlevel 2 or `scrapbook.admin`) | Report / delete pooled images no book references (papers are inline and need no GC); 60 s cooldown |

## Config (`oxide/config/Scrapbook.json`)

- `ChatCommands` (`["scrapbook","sb"]`)
- `UsePermission` (`"scrapbook.use"`) — Oxide permission required to use the book; set to `""` for everyone. Grant with `oxide.grant group default scrapbook.use`.
- `MaxPrintsPerPlayer` (200) — one shared cap across photos **and** papers; 0 = unlimited
- `MaxImageBytes` (1 MB) — largest print accepted
- `AbsorbDuplicates` (false) — whether storing a known print/note still consumes the item

All player-facing text is editable via the Lang API (`oxide/lang/en/Scrapbook.json`).

## Data files (`oxide/data/Scrapbook/`)

`players/<steamid>.json` — one book per player: `Prints` (hash, photographer, title, date) and `Papers` (hash, title, inline text, date). `images/<sha1>.jpg` — the shared content-addressed print pool; removing a page never deletes from it (other books may reference the same hash), so run `sb.gc` occasionally. Books written before v0.4.0 load unchanged.

## For developers

Release history: [CHANGELOG.md](CHANGELOG.md). Player-facing one-pager: [flyer](https://lowpoplabs.github.io/flyers/Scrapbook.html) ([PDF](Scrapbook-Flyer.pdf)). Single-file plugin, no build step: edit `Scrapbook.cs`, drop it in `oxide/plugins/`, Oxide compiles it on load.

## Support

Provided as-is. Bug reports welcome via GitHub Issues. No Discord, no custom work, no promises on turnaround. If it saved you time or you and your players enjoy it:

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lowpoplabs)

## License

[MIT](LICENSE).
