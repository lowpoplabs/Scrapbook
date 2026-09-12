using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;  // ArgEx (arg.Player()) lives in the UnityEngine namespace

namespace Oxide.Plugins
{
    [Info("Scrapbook", "LowPopLabs", "0.5.1")]
    [Description("A pocket scrapbook: collect pamphlet prints, photos and written paper notes into a CUI book, page through them, and pull out fresh copies for frames or friends.")]
    public class Scrapbook : RustPlugin
    {
        #region Config

        private ConfigData _config;

        private class ConfigData
        {
            [JsonProperty("ChatCommands (chat commands that open the book)")]
            public string[] ChatCommands = { "scrapbook", "sb" };

            [JsonProperty("UsePermission (oxide permission required to use the book; empty = everyone)")]
            public string UsePermission = "scrapbook.use";

            [JsonProperty("MaxPrintsPerPlayer (cap on prints one player's book can hold; 0 = unlimited)")]
            public int MaxPrintsPerPlayer = 200;

            [JsonProperty("MaxImageBytes (largest print the book will accept; matches the 2x pamphlet render cap by default)")]
            public int MaxImageBytes = 1048576;

            [JsonProperty("AbsorbDuplicates (true: storing a print already in the book still consumes the item; false: duplicates stay in the inventory)")]
            public bool AbsorbDuplicates = false;
        }

        protected override void LoadDefaultConfig() => _config = new ConfigData();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _config = Config.ReadObject<ConfigData>() ?? new ConfigData(); }
            catch (Exception e) { PrintWarning($"Config unreadable ({e.Message}) — using defaults."); _config = new ConfigData(); }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have a scrapbook.",
                ["StoredNew"] = "Pressed {0} new print(s) into your scrapbook.",
                ["StoredDupesLeft"] = "{0} print(s) were already in the book — left in your inventory.",
                ["StoredDupesAbsorbed"] = "{0} duplicate print(s) absorbed — the book keeps one of each.",
                ["NothingToStore"] = "No prints or notes in your inventory — pamphlets, photos and written papers go in the book.",
                ["BookFull"] = "Your scrapbook is full ({0} pages).",
                ["StoredNewPapers"] = "Pasted {0} paper note(s) into your scrapbook.",
                ["TookCopy"] = "Copied \"{0}\" out of the scrapbook — the original stays on its page.",
                ["PrintMissing"] = "That print's image file is missing on the server.",
                ["UiTitle"] = "SCRAPBOOK",
                ["UiEmpty"] = "No prints yet.\n\nCarry pamphlets or photos in your inventory\nand press  Store pages  to paste them in.",
                ["UiEmptyPapers"] = "No papers yet.\n\nCarry written notes in your inventory\nand press  Store pages  to paste them in.",
                ["UiTabPrints"] = "Prints ({0})",
                ["UiTabPapers"] = "Papers ({0})",
                ["UiPage"] = "{0} / {1}",
                ["UiCaption"] = "{0}  ·  added {1}",
                ["UiPrev"] = "<  Prev",
                ["UiNext"] = "Next  >",
                ["UiTake"] = "Take a copy",
                ["UiRemove"] = "Remove",
                ["UiRemoveConfirm"] = "Really?",
                ["Removed"] = "Tore \"{0}\" out of the scrapbook.",
                ["UiStoreAll"] = "Store pages ({0})",
                ["AdminWiped"] = "Wiped {0}'s scrapbook ({1} prints).",
                ["AdminNoBook"] = "No scrapbook on record for {0}.",
                ["AdminWipeUsage"] = "Usage: sb.wipe <name|steamid> (player must be on the server)",
                ["GcAborted"] = "GC aborted: could not read {0}.json ({1}) — deleting nothing while a book is unreadable.",
                ["GcCooldown"] = "sb.gc was run moments ago — wait {0}s before running it again.",
                ["GcDone"] = "GC: deleted {0} unreferenced image(s) ({1} KB), kept {2} in use.",
                ["GcDryRun"] = "GC dry run: {0} unreferenced image(s) ({1} KB) would be deleted, {2} in use. 'sb.gc run' to delete.",
            }, this);
        }

        private string Msg(BasePlayer player, string key, params object[] args)
        {
            var text = lang.GetMessage(key, this, player?.UserIDString);
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private void Tell(BasePlayer player, string key, params object[] args) => SendReply(player, Msg(player, key, args));

        #endregion

        #region Data

        private const string DataRoot = "Scrapbook";

        private class PhotoEntry
        {
            public string Hash;          // sha1 of the jpg bytes — the duplicate-detection key
            public ulong Photographer;   // original PhotographerSteamId, restamped onto copies
            public string Title;         // item.name at store time ("pamphlet", custom names, or "" for plain photos)
            public DateTime AddedAtUtc;
        }

        private class PaperEntry
        {
            public string Hash;          // sha1 of the utf8 text — the duplicate-detection key
            public string Title;         // item.name at store time ("note" for a plain sheet)
            public string Text;          // full note text kept inline — papers are small, no pooled file
            public DateTime AddedAtUtc;
        }

        private class PlayerBook
        {
            public List<PhotoEntry> Prints = new List<PhotoEntry>();
            public List<PaperEntry> Papers = new List<PaperEntry>();
        }

        private readonly Dictionary<ulong, PlayerBook> _books = new Dictionary<ulong, PlayerBook>();
        private readonly Dictionary<ulong, int> _pageIndex = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _paperIndex = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> _uiOpen = new HashSet<ulong>();

        // Which section of the book the player is looking at: present = Papers tab.
        private readonly HashSet<ulong> _viewingPapers = new HashSet<ulong>();

        // Remove is two-click: first click arms (button turns "Really?"), any other
        // action disarms. Guards against tearing a page out by accident.
        private readonly HashSet<ulong> _deleteArmed = new HashSet<ulong>();

        // Per-boot cache of prints pushed into FileStorage for CUI display: hash -> crc.
        // Entries are keyed to CommunityEntity's net id and removed again on Unload.
        private readonly Dictionary<string, uint> _servedCrc = new Dictionary<string, uint>();

        private string ImageDir => System.IO.Path.Combine(Interface.Oxide.DataDirectory, DataRoot, "images");

        private PlayerBook GetBook(ulong userId)
        {
            PlayerBook book;
            if (_books.TryGetValue(userId, out book)) return book;
            book = Interface.Oxide.DataFileSystem.ReadObject<PlayerBook>($"{DataRoot}/players/{userId}") ?? new PlayerBook();
            if (book.Prints == null) book.Prints = new List<PhotoEntry>();
            if (book.Papers == null) book.Papers = new List<PaperEntry>();
            _books[userId] = book;
            return book;
        }

        private void SaveBook(ulong userId)
        {
            PlayerBook book;
            if (_books.TryGetValue(userId, out book))
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/players/{userId}", book);
        }

        private string ImagePath(string hash) => System.IO.Path.Combine(ImageDir, hash + ".jpg");

        private void SaveImage(string hash, byte[] bytes)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ImageDir);
                var path = ImagePath(hash);
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllBytes(path, bytes);
            }
            catch (Exception e) { PrintWarning($"Could not save print {hash}: {e.Message}"); }
        }

        private byte[] LoadImage(string hash)
        {
            try
            {
                var path = ImagePath(hash);
                if (System.IO.File.Exists(path)) return System.IO.File.ReadAllBytes(path);
            }
            catch (Exception e) { PrintWarning($"Could not load print {hash}: {e.Message}"); }
            return null;
        }

        private static string HashOf(byte[] bytes)
        {
            using (var sha1 = SHA1.Create())
            {
                var digest = sha1.ComputeHash(bytes);
                var hex = new char[digest.Length * 2];
                for (int i = 0; i < digest.Length; i++)
                {
                    hex[i * 2] = "0123456789abcdef"[digest[i] >> 4];
                    hex[i * 2 + 1] = "0123456789abcdef"[digest[i] & 0xF];
                }
                return new string(hex);
            }
        }

        private static string HashOf(string text) => HashOf(System.Text.Encoding.UTF8.GetBytes(text));

        #endregion

        #region Lifecycle

        // Admin commands accept either this permission or auth level 2.
        private const string AdminPermission = "scrapbook.admin";

        private void Init()
        {
            if (!string.IsNullOrEmpty(_config.UsePermission))
                permission.RegisterPermission(_config.UsePermission, this);
            permission.RegisterPermission(AdminPermission, this);
            foreach (var command in _config.ChatCommands)
                if (!string.IsNullOrEmpty(command)) cmd.AddChatCommand(command, this, nameof(ChatOpen));
            Puts($"Scrapbook v{Version} loaded - by LowPopLabs - ko-fi.com/lowpoplabs");
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, UiRoot);
            // Drop this boot's CUI image cache from FileStorage — the entries are
            // keyed to CommunityEntity's net id and would orphan on the next boot.
            var community = CommunityEntity.ServerInstance;
            if (community != null && community.net != null)
                foreach (var crc in _servedCrc.Values)
                    FileStorage.server.Remove(crc, FileStorage.Type.png, community.net.ID);
            foreach (var userId in _books.Keys) SaveBook(userId);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            _uiOpen.Remove(player.userID);
            _pageIndex.Remove(player.userID);
            _paperIndex.Remove(player.userID);
            _viewingPapers.Remove(player.userID);
            _deleteArmed.Remove(player.userID);
            _books.Remove(player.userID);
        }

        #endregion

        #region Photo plumbing

        private bool Allowed(BasePlayer player) =>
            string.IsNullOrEmpty(_config.UsePermission) ||
            permission.UserHasPermission(player.UserIDString, _config.UsePermission);

        private static PhotoEntity PhotoOf(Item item)
        {
            if (item == null || item.info.shortname != "photo") return null;
            var photo = ItemModAssociatedEntity<PhotoEntity>.GetAssociatedEntity(item);
            return photo != null && photo.ImageCrc != 0 ? photo : null;
        }

        private static byte[] BytesOf(PhotoEntity photo) =>
            FileStorage.server.Get(photo.ImageCrc, FileStorage.Type.jpg, photo.net.ID);

        // Everything in main + belt that is a photo item with an actual image on it.
        private static List<Item> StorablePrints(BasePlayer player)
        {
            var found = new List<Item>();
            CollectPrints(player.inventory.containerMain, found);
            CollectPrints(player.inventory.containerBelt, found);
            return found;
        }

        private static void CollectPrints(ItemContainer container, List<Item> found)
        {
            if (container == null) return;
            foreach (var item in container.itemList)
                if (PhotoOf(item) != null) found.Add(item);
        }

        // A storable paper: the vanilla note item with actual writing on it
        // (Item.text — a public string the game serializes with the item, so
        // plugin-written notes qualify too). The length cap keeps a
        // hand-crafted monster note from bloating the data file; anything the
        // in-game editor produces is far below it.
        private const int MaxPaperChars = 8192;

        private static bool IsPaper(Item item) =>
            item != null && item.info.shortname == "note" &&
            !string.IsNullOrEmpty(item.text) && item.text.Length <= MaxPaperChars;

        private static List<Item> StorablePapers(BasePlayer player)
        {
            var found = new List<Item>();
            CollectPapers(player.inventory.containerMain, found);
            CollectPapers(player.inventory.containerBelt, found);
            return found;
        }

        private static void CollectPapers(ItemContainer container, List<Item> found)
        {
            if (container == null) return;
            foreach (var item in container.itemList)
                if (IsPaper(item)) found.Add(item);
        }

        // CUI can only show images that live in FileStorage under CommunityEntity's
        // net id. Push each print at most once per boot; RawImage's png field takes
        // the crc, and the client decodes jpg bytes just fine.
        private uint ServedCrcOf(string hash)
        {
            uint crc;
            if (_servedCrc.TryGetValue(hash, out crc)) return crc;
            var community = CommunityEntity.ServerInstance;
            if (community == null || community.net == null) return 0;
            var bytes = LoadImage(hash);
            if (bytes == null) return 0;
            crc = FileStorage.server.Store(bytes, FileStorage.Type.png, community.net.ID);
            _servedCrc[hash] = crc;
            return crc;
        }

        #endregion

        #region Store / take

        // The cap is one shared pool: photos and papers count against
        // MaxPrintsPerPlayer together.
        private bool AtCap(PlayerBook book) =>
            _config.MaxPrintsPerPlayer > 0 && book.Prints.Count + book.Papers.Count >= _config.MaxPrintsPerPlayer;

        private void StorePages(BasePlayer player)
        {
            var book = GetBook(player.userID);
            var prints = StorablePrints(player);
            var papers = StorablePapers(player);
            if (prints.Count == 0 && papers.Count == 0) { Tell(player, "NothingToStore"); return; }

            var known = new HashSet<string>();
            foreach (var entry in book.Prints) known.Add(entry.Hash);
            var knownPapers = new HashSet<string>();
            foreach (var entry in book.Papers) knownPapers.Add(entry.Hash);

            int added = 0, papersAdded = 0, dupes = 0;
            bool full = false;

            foreach (var item in prints)
            {
                if (AtCap(book)) { full = true; break; }

                var photo = PhotoOf(item);
                var bytes = photo != null ? BytesOf(photo) : null;
                if (bytes == null || bytes.Length == 0 || bytes.Length > _config.MaxImageBytes) continue;

                var hash = HashOf(bytes);
                if (known.Contains(hash))
                {
                    dupes++;
                    if (!_config.AbsorbDuplicates) continue;
                }
                else
                {
                    SaveImage(hash, bytes);
                    book.Prints.Add(new PhotoEntry
                    {
                        Hash = hash,
                        Photographer = photo.PhotographerSteamId,
                        Title = string.IsNullOrEmpty(item.name) ? "photo" : item.name,
                        AddedAtUtc = DateTime.UtcNow,
                    });
                    known.Add(hash);
                    added++;
                }
                item.RemoveFromContainer();
                item.Remove();
            }

            foreach (var item in papers)
            {
                if (AtCap(book)) { full = true; break; }

                var text = item.text;
                var hash = HashOf(text);
                if (knownPapers.Contains(hash))
                {
                    dupes++;
                    if (!_config.AbsorbDuplicates) continue;
                }
                else
                {
                    book.Papers.Add(new PaperEntry
                    {
                        Hash = hash,
                        Title = string.IsNullOrEmpty(item.name) ? "note" : item.name,
                        Text = text,
                        AddedAtUtc = DateTime.UtcNow,
                    });
                    knownPapers.Add(hash);
                    papersAdded++;
                }
                item.RemoveFromContainer();
                item.Remove();
            }

            if (added > 0 || papersAdded > 0)
            {
                SaveBook(player.userID);
                // Jump to the first of the new arrivals so storing feels like
                // pasting — prints win the view if both kinds landed.
                if (added > 0)
                {
                    _viewingPapers.Remove(player.userID);
                    _pageIndex[player.userID] = book.Prints.Count - added;
                }
                else
                {
                    _viewingPapers.Add(player.userID);
                    _paperIndex[player.userID] = book.Papers.Count - papersAdded;
                }
            }
            if (added > 0) Tell(player, "StoredNew", added);
            if (papersAdded > 0) Tell(player, "StoredNewPapers", papersAdded);
            if (dupes > 0) Tell(player, _config.AbsorbDuplicates ? "StoredDupesAbsorbed" : "StoredDupesLeft", dupes);
            if (full) Tell(player, "BookFull", _config.MaxPrintsPerPlayer);
        }

        private void TakeCopy(BasePlayer player)
        {
            var book = GetBook(player.userID);
            if (_viewingPapers.Contains(player.userID))
            {
                if (book.Papers.Count == 0) return;
                var paper = book.Papers[CurrentPaperPage(player, book)];
                var note = ItemManager.CreateByName("note", 1);
                if (note == null) return;
                note.text = paper.Text;
                if (paper.Title != "note") note.name = paper.Title;
                player.GiveItem(note, BaseEntity.GiveItemReason.PickedUp);
                Tell(player, "TookCopy", paper.Title);
                return;
            }

            if (book.Prints.Count == 0) return;
            var entry = book.Prints[CurrentPage(player, book)];
            var bytes = LoadImage(entry.Hash);
            if (bytes == null) { Tell(player, "PrintMissing"); return; }

            var item = ItemManager.CreateByName("photo", 1);
            if (item == null) return;
            if (entry.Title != "photo") item.name = entry.Title;
            var photo = ItemModAssociatedEntity<PhotoEntity>.GetAssociatedEntity(item);
            if (photo != null)
                photo.SetImageData(entry.Photographer != 0 ? entry.Photographer : player.userID, bytes);
            player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
            Tell(player, "TookCopy", entry.Title);
        }

        private void RemovePage(BasePlayer player)
        {
            var book = GetBook(player.userID);
            if (_viewingPapers.Contains(player.userID))
            {
                if (book.Papers.Count == 0) return;
                var paperPage = CurrentPaperPage(player, book);
                var paper = book.Papers[paperPage];
                book.Papers.RemoveAt(paperPage);
                SaveBook(player.userID);
                Tell(player, "Removed", paper.Title);
                return;
            }

            if (book.Prints.Count == 0) return;
            var page = CurrentPage(player, book);
            var entry = book.Prints[page];
            book.Prints.RemoveAt(page);
            SaveBook(player.userID);
            // The image file stays in the shared pool: other players' books may
            // reference the same hash, and re-storing the print later still dedupes.
            Tell(player, "Removed", entry.Title);
        }

        private static int ClampPage(Dictionary<ulong, int> index, ulong userId, int count)
        {
            int page;
            index.TryGetValue(userId, out page);
            if (page > count - 1) page = count - 1;
            if (page < 0) page = 0;
            index[userId] = page;
            return page;
        }

        private int CurrentPage(BasePlayer player, PlayerBook book) =>
            ClampPage(_pageIndex, player.userID, book.Prints.Count);

        private int CurrentPaperPage(BasePlayer player, PlayerBook book) =>
            ClampPage(_paperIndex, player.userID, book.Papers.Count);

        #endregion

        #region Commands

        private void ChatOpen(BasePlayer player, string command, string[] args)
        {
            if (!Allowed(player)) { Tell(player, "NoPermission"); return; }
            _deleteArmed.Remove(player.userID);
            OpenUi(player);
        }

        [ConsoleCommand("sb.open")]
        private void CmdOpen(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!Allowed(player)) { Tell(player, "NoPermission"); return; }
            _deleteArmed.Remove(player.userID);
            OpenUi(player);
        }

        [ConsoleCommand("sb.ui.close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            _uiOpen.Remove(player.userID);
            _deleteArmed.Remove(player.userID);
            CuiHelper.DestroyUi(player, UiRoot);
        }

        [ConsoleCommand("sb.ui.page")]
        private void CmdPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID)) return;
            _deleteArmed.Remove(player.userID);
            var book = GetBook(player.userID);
            var step = arg.GetString(0) == "next" ? 1 : -1;
            if (_viewingPapers.Contains(player.userID))
            {
                var paperPage = CurrentPaperPage(player, book) + step;
                if (paperPage < 0 || paperPage > book.Papers.Count - 1) return;
                _paperIndex[player.userID] = paperPage;
            }
            else
            {
                var page = CurrentPage(player, book) + step;
                if (page < 0 || page > book.Prints.Count - 1) return;
                _pageIndex[player.userID] = page;
            }
            OpenUi(player);
        }

        [ConsoleCommand("sb.ui.section")]
        private void CmdSection(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID)) return;
            _deleteArmed.Remove(player.userID);
            if (arg.GetString(0) == "papers") _viewingPapers.Add(player.userID);
            else _viewingPapers.Remove(player.userID);
            OpenUi(player);
        }

        [ConsoleCommand("sb.ui.store")]
        private void CmdStore(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID)) return;
            _deleteArmed.Remove(player.userID);
            StorePages(player);
            OpenUi(player);
        }

        [ConsoleCommand("sb.ui.take")]
        private void CmdTake(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID)) return;
            _deleteArmed.Remove(player.userID);
            TakeCopy(player);
            OpenUi(player);
        }

        [ConsoleCommand("sb.ui.remove")]
        private void CmdRemove(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID)) return;
            if (_deleteArmed.Remove(player.userID))
                RemovePage(player);
            else
                _deleteArmed.Add(player.userID);
            OpenUi(player);
        }

        private bool IsAdmin(ConsoleSystem.Arg arg) =>
            arg.Connection == null || arg.Connection.authLevel >= 2 ||
            permission.UserHasPermission(arg.Connection.userid.ToString(), AdminPermission);

        // Lang lookup for console replies: localized for an in-game admin,
        // default English for the server console.
        private string ConsoleMsg(ConsoleSystem.Arg arg, string key, params object[] args)
        {
            var text = lang.GetMessage(key, this, arg.Connection?.userid.ToString());
            return args.Length == 0 ? text : string.Format(text, args);
        }

        [ConsoleCommand("sb.wipe")]
        private void CmdWipe(ConsoleSystem.Arg arg)
        {
            if (!IsAdmin(arg)) return;
            var target = arg.GetString(0);
            var found = string.IsNullOrEmpty(target) ? null : BasePlayer.FindAwakeOrSleeping(target);
            if (found == null) { arg.ReplyWith(ConsoleMsg(arg, "AdminWipeUsage")); return; }
            var book = GetBook(found.userID);
            var count = book.Prints.Count + book.Papers.Count;
            if (count == 0) { arg.ReplyWith(ConsoleMsg(arg, "AdminNoBook", found.displayName)); return; }
            book.Prints.Clear();
            book.Papers.Clear();
            SaveBook(found.userID);
            _pageIndex.Remove(found.userID);
            _paperIndex.Remove(found.userID);
            if (_uiOpen.Contains(found.userID)) OpenUi(found);
            arg.ReplyWith(ConsoleMsg(arg, "AdminWiped", found.displayName, count));
        }

        // Removing a page never deletes the pooled image (other books may hold the
        // same hash), so the pool only grows. sb.gc reclaims it: reads EVERY book
        // on disk — not just the cached online ones — and deletes image files no
        // book references. Dry run by default; "sb.gc run" deletes.
        // The full-disk scan is the plugin's one expensive operation, so it sits
        // behind a cooldown — repeated presses can't stack scans on a big server.
        private const float GcCooldownSeconds = 60f;
        private float _nextGcTime;

        [ConsoleCommand("sb.gc")]
        private void CmdGc(ConsoleSystem.Arg arg)
        {
            if (!IsAdmin(arg)) return;
            var now = Time.realtimeSinceStartup;
            if (now < _nextGcTime)
            {
                arg.ReplyWith(ConsoleMsg(arg, "GcCooldown", (int)(_nextGcTime - now + 1)));
                return;
            }
            _nextGcTime = now + GcCooldownSeconds;
            var run = arg.GetString(0) == "run";

            var referenced = new HashSet<string>();
            var playersDir = System.IO.Path.Combine(Interface.Oxide.DataDirectory, DataRoot, "players");
            if (System.IO.Directory.Exists(playersDir))
            {
                foreach (var file in System.IO.Directory.GetFiles(playersDir, "*.json"))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    ulong userId;
                    PlayerBook book;
                    if (!(ulong.TryParse(name, out userId) && _books.TryGetValue(userId, out book)))
                    {
                        try { book = JsonConvert.DeserializeObject<PlayerBook>(System.IO.File.ReadAllText(file)); }
                        catch (Exception e)
                        {
                            arg.ReplyWith(ConsoleMsg(arg, "GcAborted", name, e.Message));
                            return;
                        }
                    }
                    if (book == null || book.Prints == null) continue;
                    foreach (var entry in book.Prints)
                        if (!string.IsNullOrEmpty(entry.Hash)) referenced.Add(entry.Hash);
                }
            }

            int kept = 0, dead = 0;
            long bytes = 0;
            if (System.IO.Directory.Exists(ImageDir))
            {
                foreach (var file in System.IO.Directory.GetFiles(ImageDir, "*.jpg"))
                {
                    var hash = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (referenced.Contains(hash)) { kept++; continue; }
                    dead++;
                    try { bytes += new System.IO.FileInfo(file).Length; } catch (Exception) { }
                    if (!run) continue;
                    try { System.IO.File.Delete(file); }
                    catch (Exception e) { PrintWarning($"GC could not delete {hash}: {e.Message}"); }
                    // If this boot pushed the print into the CUI cache, retire that too.
                    uint crc;
                    if (_servedCrc.TryGetValue(hash, out crc))
                    {
                        var community = CommunityEntity.ServerInstance;
                        if (community != null && community.net != null)
                            FileStorage.server.Remove(crc, FileStorage.Type.png, community.net.ID);
                        _servedCrc.Remove(hash);
                    }
                }
            }

            arg.ReplyWith(run
                ? ConsoleMsg(arg, "GcDone", dead, bytes / 1024, kept)
                : ConsoleMsg(arg, "GcDryRun", dead, bytes / 1024, kept));
        }

        #endregion

        #region UI

        private const string UiRoot = "sb.root";

        // Fixed-pixel book (900x590 on the 1280x720 CUI canvas) so the photo area
        // stays a true 16:9 — pamphlets render at 1708x960, camera photos at 854x480.
        private void OpenUi(BasePlayer player)
        {
            var book = GetBook(player.userID);
            var papersMode = _viewingPapers.Contains(player.userID);
            var page = papersMode ? CurrentPaperPage(player, book) : CurrentPage(player, book);
            var count = papersMode ? book.Papers.Count : book.Prints.Count;
            var storable = StorablePrints(player).Count + StorablePapers(player).Count;

            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.45", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", UiRoot);

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.13 0.10 0.08 0.98" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-450 -295", OffsetMax = "450 295" },
            }, UiRoot, "sb.book");

            ui.Add(new CuiLabel
            {
                Text = { Text = Msg(player, "UiTitle"), FontSize = 20, Align = UnityEngine.TextAnchor.MiddleLeft, Color = "0.87 0.83 0.74 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "24 550", OffsetMax = "220 586" },
            }, "sb.book");

            // Section tabs: the active one sits raised and lit, the other recedes.
            ui.Add(new CuiButton
            {
                Button = { Color = papersMode ? "0.20 0.18 0.15 1" : "0.33 0.29 0.23 1", Command = "sb.ui.section prints" },
                Text = { Text = Msg(player, "UiTabPrints", book.Prints.Count), FontSize = 13, Align = UnityEngine.TextAnchor.MiddleCenter, Color = papersMode ? "0.55 0.51 0.44 1" : "0.90 0.86 0.77 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "232 552", OffsetMax = "364 584" },
            }, "sb.book");

            ui.Add(new CuiButton
            {
                Button = { Color = papersMode ? "0.33 0.29 0.23 1" : "0.20 0.18 0.15 1", Command = "sb.ui.section papers" },
                Text = { Text = Msg(player, "UiTabPapers", book.Papers.Count), FontSize = 13, Align = UnityEngine.TextAnchor.MiddleCenter, Color = papersMode ? "0.90 0.86 0.77 1" : "0.55 0.51 0.44 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "370 552", OffsetMax = "502 584" },
            }, "sb.book");

            if (count > 0)
                ui.Add(new CuiLabel
                {
                    Text = { Text = Msg(player, "UiPage", page + 1, count), FontSize = 14, Align = UnityEngine.TextAnchor.MiddleRight, Color = "0.65 0.60 0.52 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "560 550", OffsetMax = "830 586" },
                }, "sb.book");

            ui.Add(new CuiButton
            {
                Button = { Color = "0.55 0.20 0.16 1", Command = "sb.ui.close" },
                Text = { Text = "✕", FontSize = 14, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.9 0.85 0.8 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "848 550", OffsetMax = "884 586" },
            }, "sb.book");

            // White matte behind the print, like a real paste-in.
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.92 0.90 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "42 82", OffsetMax = "858 548" },
            }, "sb.book", "sb.matte");

            if (count == 0)
            {
                ui.Add(new CuiLabel
                {
                    Text = { Text = Msg(player, papersMode ? "UiEmptyPapers" : "UiEmpty"), FontSize = 15, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.35 0.32 0.28 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                }, "sb.matte");
            }
            else
            {
                string title, added;
                if (papersMode)
                {
                    var paper = book.Papers[page];
                    title = paper.Title;
                    added = paper.AddedAtUtc.ToString("yyyy-MM-dd");

                    // A written sheet: title across the top of the matte, body
                    // wrapped by hand below it (see FitPaperText).
                    ui.Add(new CuiLabel
                    {
                        Text = { Text = Ink(paper.Title), FontSize = 17, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.25 0.22 0.18 1" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "70 490", OffsetMax = "830 536" },
                    }, "sb.book");

                    ui.Add(new CuiPanel
                    {
                        Image = { Color = "0.72 0.68 0.60 1" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "240 486", OffsetMax = "660 488" },
                    }, "sb.book");

                    ui.Add(new CuiLabel
                    {
                        Text = { Text = FitPaperText(Ink(paper.Text)), FontSize = 14, Font = "robotocondensed-regular.ttf", Align = UnityEngine.TextAnchor.UpperLeft, Color = "0.20 0.18 0.15 1" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "76 100", OffsetMax = "824 478" },
                    }, "sb.book");
                }
                else
                {
                    var entry = book.Prints[page];
                    title = entry.Title;
                    added = entry.AddedAtUtc.ToString("yyyy-MM-dd");

                    var crc = ServedCrcOf(entry.Hash);
                    if (crc != 0)
                        ui.Add(new CuiElement
                        {
                            Parent = "sb.book",
                            Components =
                            {
                                new CuiRawImageComponent { Png = crc.ToString() },
                                new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "50 90", OffsetMax = "850 540" },
                            },
                        });
                    else
                        ui.Add(new CuiLabel
                        {
                            Text = { Text = Msg(player, "PrintMissing"), FontSize = 14, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.35 0.32 0.28 1" },
                            RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                        }, "sb.matte");
                }

                ui.Add(new CuiLabel
                {
                    Text = { Text = Msg(player, "UiCaption", Ink(title), added), FontSize = 12, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.55 0.51 0.44 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "50 56", OffsetMax = "850 82" },
                }, "sb.book");

                if (page > 0)
                    ui.Add(new CuiButton
                    {
                        Button = { Color = "0.25 0.22 0.18 1", Command = "sb.ui.page prev" },
                        Text = { Text = Msg(player, "UiPrev"), FontSize = 14, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.85 0.81 0.72 1" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "24 10", OffsetMax = "124 52" },
                    }, "sb.book");

                if (page < count - 1)
                    ui.Add(new CuiButton
                    {
                        Button = { Color = "0.25 0.22 0.18 1", Command = "sb.ui.page next" },
                        Text = { Text = Msg(player, "UiNext"), FontSize = 14, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.85 0.81 0.72 1" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "776 10", OffsetMax = "876 52" },
                    }, "sb.book");

                ui.Add(new CuiButton
                {
                    Button = { Color = "0.36 0.43 0.26 1", Command = "sb.ui.take" },
                    Text = { Text = Msg(player, "UiTake"), FontSize = 14, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.92 0.92 0.85 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "250 10", OffsetMax = "430 52" },
                }, "sb.book");

                var armed = _deleteArmed.Contains(player.userID);
                ui.Add(new CuiButton
                {
                    Button = { Color = armed ? "0.72 0.20 0.14 1" : "0.33 0.18 0.15 1", Command = "sb.ui.remove" },
                    Text = { Text = Msg(player, armed ? "UiRemoveConfirm" : "UiRemove"), FontSize = 13, Align = UnityEngine.TextAnchor.MiddleCenter, Color = armed ? "1 0.92 0.88 1" : "0.78 0.62 0.56 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "662 10", OffsetMax = "756 52" },
                }, "sb.book");
            }

            ui.Add(new CuiButton
            {
                Button = { Color = storable > 0 ? "0.30 0.34 0.42 1" : "0.20 0.19 0.17 1", Command = "sb.ui.store" },
                Text = { Text = Msg(player, "UiStoreAll", storable), FontSize = 14, Align = UnityEngine.TextAnchor.MiddleCenter, Color = storable > 0 ? "0.88 0.90 0.94 1" : "0.5 0.48 0.44 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "470 10", OffsetMax = "650 52" },
            }, "sb.book");

            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.AddUi(player, ui);
            _uiOpen.Add(player.userID);
        }

        // Display-only guard: note text is player-written, and CUI labels parse
        // rich-text tags. Swapping the bracket kills the markup without touching
        // the stored text — a taken copy still round-trips exactly.
        private static string Ink(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "‹");

        // Rust CUI has no scrolling and Unity clips overflowing lines silently,
        // so budget in WRAPPED lines: conservative chars-per-line estimate,
        // wrap by hand, and end an over-long note with an ellipsis instead of
        // letting it vanish off the bottom of the matte.
        private const int PaperCharsPerLine = 78; // conservative at FontSize 14 across the 748px body (720p reference canvas)
        private const int PaperLineBudget = 18;

        private static string FitPaperText(string text)
        {
            var sb = new System.Text.StringBuilder();
            int lines = 0;
            var truncated = false;
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                if (lines >= PaperLineBudget) { truncated = true; break; }
                if (paragraph.Length == 0)
                {
                    if (sb.Length > 0) { sb.Append('\n'); lines++; }
                    continue;
                }
                int start = 0;
                while (start < paragraph.Length)
                {
                    if (lines >= PaperLineBudget) { truncated = true; break; }
                    int len = Math.Min(PaperCharsPerLine, paragraph.Length - start);
                    if (start + len < paragraph.Length)
                    {
                        var space = paragraph.LastIndexOf(' ', start + len, len);
                        if (space > start) len = space - start;
                    }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(paragraph, start, len);
                    lines++;
                    start += len;
                    while (start < paragraph.Length && paragraph[start] == ' ') start++;
                }
                if (truncated) break;
            }
            if (truncated) sb.Append(" …");
            return sb.ToString();
        }

        #endregion
    }
}
