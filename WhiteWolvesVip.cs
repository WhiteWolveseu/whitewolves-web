using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WhiteWolvesVip", "WhiteWolves", "1.1.4")]
    [Description("Manually confirmed bank transfers: 5000 RP and a timed VIP chat title.")]
    public class WhiteWolvesVip : RustPlugin
    {
        [PluginReference] private Plugin ServerRewards, BetterChat;
        private const int Points = 5000;
        private const int Days = 30;
        private Ledger data;
        private bool blocked;
        private static readonly Regex PaymentId = new Regex(@"^[A-Za-z0-9_-]{1,80}$");

        private class Ledger
        {
            public Dictionary<string, long> Expiries = new Dictionary<string, long>();
            public Dictionary<string, Purchase> Payments = new Dictionary<string, Purchase>();
        }

        private class Purchase
        {
            public string SteamId;
            public string State; // Pending -> Credited -> Complete. Pending is NEVER retried automatically.
            public int BalanceBefore;
            public long ConfirmedAt;
            public long ExpiresAt;
        }

        private long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }

        private void Init()
        {
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<Ledger>(Name);
                if (data == null || data.Expiries == null || data.Payments == null)
                    throw new Exception("Invalid ledger. Restore a backup; do not reset paid records.");
                foreach (var entry in data.Payments)
                    if (entry.Value == null || !ValidSteamId(entry.Value.SteamId) ||
                        (entry.Value.State != "Pending" && entry.Value.State != "Credited" && entry.Value.State != "Complete"))
                        throw new Exception("Invalid payment record: " + entry.Key);
            }
            catch (Exception ex)
            {
                blocked = true;
                PrintError("VIP LEDGER BLOCKED: " + ex.Message);
            }
        }

        private bool Save()
        {
            if (blocked) return false;
            try { Interface.Oxide.DataFileSystem.WriteObject(Name, data); return true; }
            catch (Exception ex)
            {
                blocked = true;
                PrintError("WRITE FAILED. Stop confirmations and restore storage: " + ex.Message);
                return false;
            }
        }

        private void OnServerInitialized()
        {
            RegisterTitle();
            foreach (BasePlayer player in BasePlayer.activePlayerList) ShowHud(player);
            timer.Every(1f, RefreshHudStatuses);
            if (blocked) return;
            // No RP calls during recovery. Only finish already recorded credits.
            foreach (var purchase in data.Payments.Values)
            {
                if (purchase.State == "Credited") Finish(purchase);
                if (purchase.State == "Pending")
                    PrintWarning("Uncertain RP delivery for " + purchase.SteamId + ". Use wwvip.payment and reconcile manually.");
            }
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "BetterChat") NextTick(RegisterTitle);
        }

        private void RegisterTitle()
        {
            if (BetterChat == null || !BetterChat.IsLoaded)
            {
                PrintWarning("Install Better Chat for the VIP prefix. New purchases are blocked until it is loaded.");
                return;
            }
            BetterChat.Call("API_RegisterThirdPartyTitle", this,
                new Func<IPlayer, string>(player => IsVip(player.Id) ? "[#FFD166][VIP][/#]" : null));
        }

        private bool IsVip(string id)
        {
            long expiry;
            return !blocked && data != null && data.Expiries.TryGetValue(id, out expiry) && expiry > Now;
        }

        private static bool ValidSteamId(string id)
        {
            ulong value;
            return id != null && id.Length == 17 && ulong.TryParse(id, out value) &&
                   value > 76561197960265728UL && value < 76561202255233024UL;
        }

        private static string Date(long seconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        private bool Ready(ConsoleSystem.Arg arg)
        {
            // Only server console / RCON, never an in-game player (including admins).
            if (arg.Connection != null) { arg.ReplyWith("Server console / RCON only."); return false; }
            if (blocked || data == null) { arg.ReplyWith("Ledger blocked. Inspect logs and restore data before continuing."); return false; }
            return true;
        }

        [ConsoleCommand("wwvip.confirm")]
        private void Confirm(ConsoleSystem.Arg arg)
        {
            if (!Ready(arg)) return;
            if (arg.Args == null || arg.Args.Length != 2 || !ValidSteamId(arg.Args[0].ToString()) || !PaymentId.IsMatch(arg.Args[1].ToString()))
            { arg.ReplyWith("Usage: wwvip.confirm STEAMID64 UNIQUE_BANK_TRANSACTION_ID (only after receiving 499 CZK)"); return; }
            string id = arg.Args[0].ToString(), key = arg.Args[1].ToString().ToUpperInvariant();
            if (data.Payments.ContainsKey(key))
            { arg.ReplyWith("Payment already recorded: " + key + " / " + data.Payments[key].State + ". No second credit."); return; }
            foreach (var prior in data.Payments.Values)
                if (prior.SteamId == id && prior.State != "Complete")
                { arg.ReplyWith("This player has an unresolved payment. Reconcile it first."); return; }
            if (ServerRewards == null || !ServerRewards.IsLoaded || BetterChat == null || !BetterChat.IsLoaded)
            { arg.ReplyWith("ServerRewards AND BetterChat must be loaded."); return; }
            object balance = ServerRewards.Call("CheckPoints", id);
            if (!(balance is int) || (int)balance < 0 || (int)balance > int.MaxValue - Points)
            { arg.ReplyWith("Cannot safely read RP balance. No payment recorded."); return; }
            long oldExpiry;
            data.Expiries.TryGetValue(id, out oldExpiry);
            var purchase = new Purchase {
                SteamId = id, State = "Pending", BalanceBefore = (int)balance,
                ConfirmedAt = Now, ExpiresAt = Math.Max(Now, oldExpiry) + Days * 86400L
            };
            data.Payments.Add(key, purchase);
            if (!Save()) { arg.ReplyWith("Ledger save failed; no RP call made. Inspect logs."); return; }
            object result;
            try { result = ServerRewards.Call("AddPoints", id, Points); }
            catch (Exception ex)
            { PrintError(ex.Message); arg.ReplyWith("Uncertain result. Payment remains Pending; do not repeat under a new ID."); return; }
            if (!(result is bool) || !(bool)result)
            { arg.ReplyWith("RP call not confirmed. Payment remains Pending. Check actual RP balance before resolving."); return; }
            // Ask ServerRewards to flush balances using its save hook. This is not a cross-plugin atomic transaction.
            ServerRewards.Call("OnServerSave");
            purchase.State = "Credited";
            if (!Save()) { arg.ReplyWith("RP added but ledger write failed. Reconcile before any retry."); return; }
            if (!Finish(purchase)) { arg.ReplyWith("VIP save failed. Inspect logs."); return; }
            arg.ReplyWith("OK " + key + ": " + id + ", +5000 RP, VIP until " + Date(purchase.ExpiresAt));
            LogToFile("payments", key + " " + id + " 499 CZK 5000 RP " + Date(purchase.ExpiresAt), this);
            BasePlayer player = BasePlayer.FindByID(ulong.Parse(id));
            if (player != null && player.IsConnected)
                SendReply(player, "WhiteWolves: +5000 RP. [VIP] until / do " + Date(purchase.ExpiresAt));
        }

        private bool Finish(Purchase purchase)
        {
            long current;
            data.Expiries.TryGetValue(purchase.SteamId, out current);
            data.Expiries[purchase.SteamId] = Math.Max(current, purchase.ExpiresAt);
            purchase.State = "Complete";
            bool saved = Save();
            if (saved) RefreshHudStatuses();
            return saved;
        }

        [ConsoleCommand("wwvip.payment")]
        private void Payment(ConsoleSystem.Arg arg)
        {
            if (!Ready(arg)) return;
            Purchase p;
            if (arg.Args == null || arg.Args.Length != 1 || !data.Payments.TryGetValue(arg.Args[0].ToString().ToUpperInvariant(), out p))
            { arg.ReplyWith("Usage: wwvip.payment TRANSACTION_ID (must already exist)"); return; }
            arg.ReplyWith("SteamID=" + p.SteamId + " state=" + p.State + " RP-before=" + p.BalanceBefore +
                " expected-credit=5000 confirmed=" + Date(p.ConfirmedAt) + " VIP-end=" + Date(p.ExpiresAt));
        }

        [ConsoleCommand("wwvip.resolve")]
        private void Resolve(ConsoleSystem.Arg arg)
        {
            if (!Ready(arg)) return;
            Purchase p;
            if (arg.Args == null || arg.Args.Length != 2 || !data.Payments.TryGetValue(arg.Args[0].ToString().ToUpperInvariant(), out p) ||
                (arg.Args[1].ToString() != "credited" && arg.Args[1].ToString() != "notcredited"))
            { arg.ReplyWith("Usage: wwvip.resolve TRANSACTION_ID credited|notcredited. First verify RP records manually!"); return; }
            if (p.State != "Pending") { arg.ReplyWith("Only Pending payments can be resolved."); return; }
            if (arg.Args[1].ToString() == "credited")
            {
                p.State = "Credited";
                if (Save() && Finish(p)) arg.ReplyWith("VIP applied. No RP added by resolve.");
            }
            else
            {
                data.Payments.Remove(arg.Args[0].ToString().ToUpperInvariant());
                if (Save()) arg.ReplyWith("Reservation removed. Confirm the SAME transaction ID to retry after checking payment.");
            }
            LogToFile("payments", "MANUAL RESOLVE " + arg.Args[0].ToString() + " " + arg.Args[1].ToString(), this);
        }

        [ConsoleCommand("wwvip.status")]
        private void Status(ConsoleSystem.Arg arg)
        {
            if (!Ready(arg)) return;
            if (arg.Args == null || arg.Args.Length != 1 || !ValidSteamId(arg.Args[0].ToString()))
            { arg.ReplyWith("Usage: wwvip.status STEAMID64"); return; }
            long expiry;
            arg.ReplyWith(data.Expiries.TryGetValue(arg.Args[0].ToString(), out expiry)
                ? "VIP active=" + IsVip(arg.Args[0].ToString()) + "; ends " + Date(expiry) : "No VIP record.");
        }

        [ChatCommand("vip")]
        private void Vip(BasePlayer player, string command, string[] args)
        {
            ShowWindow(player, ChooseCzech(player, args.Length > 0 ? args[0].ToLowerInvariant() : "default"), "pay");
        }

        private const string VipPanel = "WhiteWolvesVip.Window";
        private const string VipHud = "WhiteWolvesVip.Hud";
        private UiSettings uiSettings;
        private readonly Dictionary<ulong, bool> hudVipStates = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, double> uiCooldown = new Dictionary<ulong, double>();

        private class UiSettings
        {
            public bool ShowHudButton = true;
            public string HudAnchor = "0.5 0";
            public string HudOffsetMin = "-123 1";
            public string HudOffsetMax = "-3 17";
            public string HudButtonText = "PURCHASE VIP";
            public int HudFontSize = 11;
            public string VipStatusText = "VIP players";
            public string EconomyStatusText = "Economy players";
        }

        protected override void LoadDefaultConfig()
        {
            uiSettings = new UiSettings();
            Config.WriteObject(uiSettings, true);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            uiSettings = Config.ReadObject<UiSettings>();
            if (uiSettings == null) throw new Exception("Invalid WhiteWolvesVip UI config.");
            // Migrate only the exact previous default layout; preserve custom positions.
            if ((uiSettings.HudAnchor == "1 0" && uiSettings.HudOffsetMin == "-210 355" && uiSettings.HudOffsetMax == "-20 393") ||
                (uiSettings.HudAnchor == "0.5 0" && uiSettings.HudOffsetMin == "-60 1" && uiSettings.HudOffsetMax == "60 17"))
            {
                uiSettings.HudAnchor = "0.5 0";
                uiSettings.HudOffsetMin = "-123 1";
                uiSettings.HudOffsetMax = "-3 17";
                Config.WriteObject(uiSettings, true);
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            timer.Once(3f, () => { if (player != null && player.IsConnected) ShowHud(player); });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            CuiHelper.DestroyUi(player, VipPanel);
            CuiHelper.DestroyUi(player, VipHud);
            uiCooldown.Remove(player.userID);
            hudVipStates.Remove(player.userID);
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, VipPanel);
                CuiHelper.DestroyUi(player, VipHud);
            }
        }

        private void ShowHud(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, VipHud);
            hudVipStates.Remove(player.userID);
            if (uiSettings == null || !uiSettings.ShowHudButton) return;
            var ui = new CuiElementContainer();
            ui.Add(new CuiButton {
                Button = { Color = "0.78 0.64 0.25 0.96", Command = "wwvip.ui default pay" },
                RectTransform = { AnchorMin = uiSettings.HudAnchor, AnchorMax = uiSettings.HudAnchor,
                    OffsetMin = uiSettings.HudOffsetMin, OffsetMax = uiSettings.HudOffsetMax },
                Text = { Text = uiSettings.HudButtonText, FontSize = uiSettings.HudFontSize, Align = TextAnchor.MiddleCenter, Color = "0.08 0.09 0.10 1" }
            }, "Overlay", VipHud);
            AddHudStatus(ui, IsVip(player.UserIDString));
            CuiHelper.AddUi(player, ui);
            hudVipStates[player.userID] = IsVip(player.UserIDString);
        }

        private void AddHudStatus(CuiElementContainer ui, bool vip)
        {
            string statusPanel = VipHud + ".Status";
            ui.Add(new CuiPanel {
                Image = { Color = vip ? "0.23 0.20 0.12 0.94" : "0.10 0.13 0.16 0.94" },
                RectTransform = { AnchorMin = "1 0", AnchorMax = "1 1", OffsetMin = "6 0", OffsetMax = "126 0" }
            }, VipHud, statusPanel);
            ui.Add(new CuiLabel {
                Text = { Text = vip ? uiSettings.VipStatusText : uiSettings.EconomyStatusText,
                    FontSize = uiSettings.HudFontSize, Align = TextAnchor.MiddleCenter,
                    Color = vip ? "1 0.82 0.41 1" : "0.75 0.80 0.85 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, statusPanel);
        }

        private void RefreshHudStatuses()
        {
            if (uiSettings == null || !uiSettings.ShowHudButton) return;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                bool previous;
                if (!hudVipStates.TryGetValue(player.userID, out previous)) continue;
                bool vip = IsVip(player.UserIDString);
                if (vip == previous) continue;
                CuiHelper.DestroyUi(player, VipHud + ".Status");
                var ui = new CuiElementContainer();
                AddHudStatus(ui, vip);
                CuiHelper.AddUi(player, ui);
                hudVipStates[player.userID] = vip;
            }
        }

        [ConsoleCommand("wwvip.ui")]
        private void UiCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !player.IsConnected) return;
            double current = UnityEngine.Time.realtimeSinceStartup;
            double last;
            if (uiCooldown.TryGetValue(player.userID, out last) && current - last < 0.4) return;
            uiCooldown[player.userID] = current;
            string language = arg.Args != null && arg.Args.Length > 0 ? arg.Args[0].ToString() : "default";
            string tab = arg.Args != null && arg.Args.Length > 1 ? arg.Args[1].ToString() : "pay";
            ShowWindow(player, ChooseCzech(player, language), tab);
        }

        private bool ChooseCzech(BasePlayer player, string language)
        {
            if (language == "cs") return true;
            if (language == "en") return false;
            return lang.GetLanguage(player.UserIDString).StartsWith("cs", StringComparison.OrdinalIgnoreCase);
        }

        private static string Pair(float x, float y)
        { return x.ToString("0.###", CultureInfo.InvariantCulture) + " " + y.ToString("0.###", CultureInfo.InvariantCulture); }

        private static void Box(CuiElementContainer ui, string parent, float x, float y, float w, float h, string color)
        {
            ui.Add(new CuiPanel {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = Pair(x, y), OffsetMax = Pair(x + w, y + h) }
            }, parent);
        }

        private static void Label(CuiElementContainer ui, string parent, float x, float y, float w, float h,
            string text, int size = 15, string color = "0.94 0.95 0.96 1", TextAnchor align = TextAnchor.MiddleLeft)
        {
            ui.Add(new CuiLabel {
                Text = { Text = text, FontSize = size, Color = color, Align = align },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = Pair(x, y), OffsetMax = Pair(x + w, y + h) }
            }, parent);
        }

        private static void Button(CuiElementContainer ui, string parent, float x, float y, float w, float h,
            string text, string command, bool selected = false)
        {
            ui.Add(new CuiButton {
                Button = { Color = selected ? "0.72 0.56 0.22 1" : "0.18 0.21 0.25 1", Command = command },
                Text = { Text = text, FontSize = 13, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = Pair(x, y), OffsetMax = Pair(x + w, y + h) }
            }, parent);
        }

        private static void Row(CuiElementContainer ui, float y, string label, string value)
        {
            Box(ui, VipPanel, 30, y, 525, 32, "0.12 0.14 0.17 1");
            Label(ui, VipPanel, 42, y, 127, 32, label, 13, "0.63 0.68 0.73 1");
            Label(ui, VipPanel, 170, y, 376, 32, value, 15);
        }

        private static void DrawQr(CuiElementContainer ui, string parent, string payload)
        {
            bool[,] matrix = VipQr.Encode(payload);
            // 37 modules + 4-module quiet zone on each side. Merge dark runs to keep CUI lightweight.
            const int cell = 6;
            Box(ui, parent, 600, 190, 270, 270, "1 1 1 1");
            for (int row = 0; row < 37; row++)
            {
                int column = 0;
                while (column < 37)
                {
                    if (!matrix[row, column]) { column++; continue; }
                    int first = column;
                    while (column < 37 && matrix[row, column]) column++;
                    Box(ui, parent, 600 + (first + 4) * cell, 190 + (40 - row) * cell,
                        (column - first) * cell, cell, "0 0 0 1");
                }
            }
        }

        private void ShowWindow(BasePlayer player, bool cs, string tab)
        {
            if (tab != "web" && tab != "discord") tab = "pay";
            CuiHelper.DestroyUi(player, VipPanel);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel {
                Image = { Color = "0.065 0.075 0.09 0.995" }, CursorEnabled = true,
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-450 -310", OffsetMax = "450 310" }
            }, "Overlay", VipPanel);
            Box(ui, VipPanel, 0, 615, 900, 5, "0.90 0.73 0.35 1");
            Label(ui, VipPanel, 30, 566, 700, 38, "WHITEWOLVES  /  VIP", 26, "1 0.82 0.41 1");
            Label(ui, VipPanel, 30, 536, 800, 25,
                cs ? "Podpoř server a získej odměnu ve hře." : "Support the server and receive in-game rewards.", 15, "0.63 0.68 0.73 1");
            ui.Add(new CuiButton {
                Button = { Color = "0.24 0.14 0.15 1", Close = VipPanel },
                Text = { Text = "X", FontSize = 20, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "835 565", OffsetMax = "875 605" }
            }, VipPanel);

            Box(ui, VipPanel, 30, 450, 525, 76, "0.18 0.18 0.13 1");
            Label(ui, VipPanel, 45, 483, 380, 35, "5 000 RP + [VIP]", 25, "1 0.82 0.41 1");
            Label(ui, VipPanel, 45, 455, 325, 27, cs ? "Prefix na 30 dní • jednorázový nákup" : "30-day prefix • one-time purchase", 14);
            Label(ui, VipPanel, 395, 463, 143, 48, cs ? "499 Kč" : "499 CZK", 27, "1 1 1 1", TextAnchor.MiddleRight);

            string language = cs ? "cs" : "en";
            Button(ui, VipPanel, 600, 477, 86, 36, cs ? "PLATBA" : "PAYMENT", "wwvip.ui " + language + " pay", tab == "pay");
            Button(ui, VipPanel, 692, 477, 86, 36, "WEB QR", "wwvip.ui " + language + " web", tab == "web");
            Button(ui, VipPanel, 784, 477, 86, 36, "DISCORD", "wwvip.ui " + language + " discord", tab == "discord");

            Row(ui, 408, cs ? "Příjemce" : "Beneficiary", "WhiteWolves.eu");
            Row(ui, 370, cs ? "Účet / banka" : "Account / bank", "670100-2213947635/6210 · mBank");
            Row(ui, 332, "IBAN", "CZ53 6210 6701 0022 1394 7635");
            Row(ui, 294, "BIC / SWIFT", "BREXCZPPXXX");
            Row(ui, 256, "SteamID", player.UserIDString);
            Row(ui, 218, cs ? "Zpráva k platbě" : "Payment message", "VIP " + player.UserIDString);
            long expiry;
            string status = blocked ? (cs ? "Evidence VIP je nedostupná." : "VIP ledger is unavailable.")
                : data.Expiries.TryGetValue(player.UserIDString, out expiry) && IsVip(player.UserIDString)
                    ? (cs ? "VIP aktivní do: " : "VIP active until: ") + Date(expiry)
                    : (cs ? "VIP zatím není aktivní." : "VIP is not active yet.");
            Label(ui, VipPanel, 30, 171, 525, 36, status, 14, "0.85 0.80 0.57 1");

            string payload = tab == "web" ? "https://whitewolves.eu/" : tab == "discord" ? "https://discord.gg/jJxbZQG5sC"
                : "SPD*1.0*ACC:CZ5362106701002213947635*AM:499.00*CC:CZK*MSG:VIP " + player.UserIDString;
            DrawQr(ui, VipPanel, payload);
            string caption = tab == "pay" ? (cs ? "Naskenuj v české bankovní aplikaci.\nČástka i SteamID jsou předvyplněné." : "Scan with a Czech banking app.\nAmount and SteamID are prefilled.")
                : tab == "web" ? "https://whitewolves.eu/" : "https://discord.gg/jJxbZQG5sC";
            Label(ui, VipPanel, 580, 142, 310, 44, caption, 13, "0.72 0.76 0.80 1", TextAnchor.MiddleCenter);
            Box(ui, VipPanel, 30, 132, 840, 1, "0.24 0.27 0.30 1");
            Label(ui, VipPanel, 30, 64, 840, 62, cs
                ? "Po přijetí a ručním ověření převodu připíšeme RP a aktivujeme VIP.\nZe zahraničí použij IBAN/BIC a platbu v CZK; ověř dostupnost a poplatky u své banky.\nPo zaplacení nám pošli SteamID a identifikaci platby na Discord."
                : "RP and VIP are delivered after receipt and manual verification of your transfer.\nInternational players: use IBAN/BIC and CZK; check availability and fees with your bank.\nAfter paying, send your SteamID and payment reference via Discord.", 13, "0.66 0.71 0.76 1");
            Button(ui, VipPanel, 30, 20, 65, 32, "ČESKY", "wwvip.ui cs " + tab, cs);
            Button(ui, VipPanel, 102, 20, 75, 32, "ENGLISH", "wwvip.ui en " + tab, !cs);
            Label(ui, VipPanel, 210, 20, 660, 32,
                cs ? "Web otevři přes QR. Tlačítka pouze přepínají obsah tohoto okna." : "Use the QR to open links. Buttons switch between this window's tabs.",
                12, "0.53 0.58 0.64 1", TextAnchor.MiddleRight);
            CuiHelper.AddUi(player, ui);
        }

        // Fixed QR version 5, byte mode, ECC L, mask 0. One RS block: 108 data + 26 parity bytes.
        // Local generation: no account or SteamID is sent to a QR service.
        private static class VipQr
        {
            private const int N = 37;
            private static void Bits(List<int> list, int value, int count)
            { for (int i = count - 1; i >= 0; i--) list.Add((value >> i) & 1); }

            private static int Mul(int a, int b)
            {
                int result = 0;
                while (b != 0)
                {
                    if ((b & 1) != 0) result ^= a;
                    b >>= 1; a <<= 1;
                    if ((a & 256) != 0) a ^= 0x11D;
                }
                return result;
            }

            private static void Put(bool[,] grid, bool[,] reserved, int x, int y, bool dark)
            {
                if (x < 0 || y < 0 || x >= N || y >= N) return;
                grid[y, x] = dark; reserved[y, x] = true;
            }

            private static void Finder(bool[,] grid, bool[,] reserved, int ox, int oy)
            {
                for (int y = -1; y <= 7; y++)
                    for (int x = -1; x <= 7; x++)
                        Put(grid, reserved, ox + x, oy + y,
                            x >= 0 && x <= 6 && y >= 0 && y <= 6 &&
                            (x == 0 || x == 6 || y == 0 || y == 6 ||
                             (x >= 2 && x <= 4 && y >= 2 && y <= 4)));
            }

            public static bool[,] Encode(string text)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                if (bytes.Length > 106) throw new ArgumentException("QR payload exceeds fixed capacity.");
                var bits = new List<int>();
                Bits(bits, 4, 4); Bits(bits, bytes.Length, 8);
                foreach (byte b in bytes) Bits(bits, b, 8);
                Bits(bits, 0, Math.Min(4, 864 - bits.Count));
                while (bits.Count % 8 != 0) bits.Add(0);
                int pad = 0;
                while (bits.Count < 864) Bits(bits, (pad++ % 2 == 0) ? 236 : 17, 8);
                int[] dataBytes = new int[108];
                for (int i = 0; i < 108; i++)
                    for (int j = 0; j < 8; j++) dataBytes[i] = (dataBytes[i] << 1) | bits[i * 8 + j];

                int[] generator = new int[] { 1 };
                int root = 1;
                for (int i = 0; i < 26; i++)
                {
                    int[] next = new int[generator.Length + 1];
                    for (int j = 0; j < generator.Length; j++)
                    { next[j] ^= generator[j]; next[j + 1] ^= Mul(generator[j], root); }
                    generator = next; root = Mul(root, 2);
                }
                int[] work = new int[134];
                Array.Copy(dataBytes, work, 108);
                for (int i = 0; i < 108; i++)
                {
                    int factor = work[i];
                    for (int j = 0; j < generator.Length; j++) work[i + j] ^= Mul(generator[j], factor);
                }
                var stream = new List<int>();
                foreach (int b in dataBytes) Bits(stream, b, 8);
                for (int i = 108; i < 134; i++) Bits(stream, work[i], 8);

                bool[,] grid = new bool[N, N], reserved = new bool[N, N];
                Finder(grid, reserved, 0, 0); Finder(grid, reserved, N - 7, 0); Finder(grid, reserved, 0, N - 7);
                for (int i = 8; i < N - 8; i++)
                { Put(grid, reserved, i, 6, i % 2 == 0); Put(grid, reserved, 6, i, i % 2 == 0); }
                for (int y = -2; y <= 2; y++)
                    for (int x = -2; x <= 2; x++)
                        Put(grid, reserved, 30 + x, 30 + y, Math.Max(Math.Abs(x), Math.Abs(y)) != 1);

                // ECC L (01) and mask 000; BCH format polynomial 0x537, xor mask 0x5412.
                int remainder = 8;
                for (int i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
                int format = ((8 << 10) | remainder) ^ 0x5412;
                for (int i = 0; i <= 5; i++) Put(grid, reserved, 8, i, ((format >> i) & 1) != 0);
                Put(grid, reserved, 8, 7, ((format >> 6) & 1) != 0);
                Put(grid, reserved, 8, 8, ((format >> 7) & 1) != 0);
                Put(grid, reserved, 7, 8, ((format >> 8) & 1) != 0);
                for (int i = 9; i < 15; i++) Put(grid, reserved, 14 - i, 8, ((format >> i) & 1) != 0);
                for (int i = 0; i < 8; i++) Put(grid, reserved, N - 1 - i, 8, ((format >> i) & 1) != 0);
                for (int i = 8; i < 15; i++) Put(grid, reserved, 8, N - 15 + i, ((format >> i) & 1) != 0);
                Put(grid, reserved, 8, N - 8, true);

                int index = 0;
                bool up = true;
                for (int right = N - 1; right >= 1; right -= 2)
                {
                    if (right == 6) right = 5;
                    for (int vertical = 0; vertical < N; vertical++)
                    {
                        int y = up ? N - 1 - vertical : vertical;
                        for (int j = 0; j < 2; j++)
                        {
                            int x = right - j;
                            if (reserved[y, x]) continue;
                            bool dark = index < stream.Count && stream[index] != 0;
                            index++;
                            grid[y, x] = dark ^ ((x + y) % 2 == 0);
                        }
                    }
                    up = !up;
                }
                return grid;
            }
        }

    }
}
