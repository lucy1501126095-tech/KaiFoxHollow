using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using Microsoft.Xna.Framework.Input;
using StardewValley.Menus;
using System.Runtime.InteropServices;
using xTile.Dimensions;
using StardewValley.Monsters;

namespace NagiBridge;

public class ModEntry : Mod
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly Queue<Action> _mainThreadQueue = new();
    private readonly object _queueLock = new();
    private int _port;

    // Pathfinding state
    private Queue<Point>? _pathQueue;
    private int _pathTickCooldown;

    // Command queue state
    private Queue<Dictionary<string, object?>>? _commandQueue;
    private readonly List<object> _commandResults = new();
    private TaskCompletionSource<object>? _commandQueueTcs;
    private int _commandDelay;
    private bool _waitingForMove;
    private bool _waitingForBite;
    private int _biteTimeout;

    // Time freeze state
    private bool _timeFrozen;
    private int _frozenTime;

    // Journey of the Prairie King bot state
    private bool _minigameBotActive;
    private string? _minigameBotLastError;
    private int _minigameBotErrorCooldown;
    private Vector2 _minigameBotLastMove;

    private static Type? _abigailGameType;
    private static Type? _abigailGameReflectedType;
    private static FieldInfo? _pkPlayerPositionField;
    private static FieldInfo? _pkPlayerBoundingBoxField;
    private static FieldInfo? _pkPlayerMovementDirectionsField;
    private static FieldInfo? _pkPlayerShootingDirectionsField;
    private static FieldInfo? _pkMonstersField;
    private static FieldInfo? _pkBulletsField;
    private static FieldInfo? _pkPowerupsField;
    private static FieldInfo? _pkShoppingField;
    private static FieldInfo? _pkDeathTimerField;
    private static FieldInfo? _pkBetweenWaveTimerField;
    private static FieldInfo? _pkGameOverField;
    private static FieldInfo? _pkLivesField;
    private static FieldInfo? _pkCoinsField;
    private static FieldInfo? _pkWhichWaveField;
    private static readonly Dictionary<string, FieldInfo?> _pkObjectFieldCache = new();

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        StartServer();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _pathQueue = null;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // Drain main-thread action queue
        lock (_queueLock)
        {
            while (_mainThreadQueue.Count > 0)
            {
                try { _mainThreadQueue.Dequeue().Invoke(); }
                catch (Exception ex) { Monitor.Log($"Queued action error: {ex}", LogLevel.Error); }
            }
        }

        // Freeze time if paused
        if (_timeFrozen && Context.IsWorldReady)
            Game1.timeOfDay = _frozenTime;

        if (_minigameBotActive && IsPrairieKing(Game1.currentMinigame))
        {
            try
            {
                TickPrairieKingBot(Game1.currentMinigame!);
            }
            catch (Exception ex)
            {
                _minigameBotLastError = ex.Message;
                if (_minigameBotErrorCooldown <= 0)
                {
                    Monitor.Log($"Prairie King bot error: {ex}", LogLevel.Warn);
                    _minigameBotErrorCooldown = 300;
                }
            }
        }

        if (_minigameBotErrorCooldown > 0)
            _minigameBotErrorCooldown--;

        // Process pathfinding movement
        if (_pathQueue != null && _pathQueue.Count > 0 && Context.IsWorldReady)
        {
            if (_pathTickCooldown > 0)
            {
                _pathTickCooldown--;
                return;
            }

            var next = _pathQueue.Peek();
            var farmer = Game1.player;
            var target = new Vector2(next.X * 64 + 32, next.Y * 64 + 32);
            var diff = target - farmer.Position;

            if (diff.Length() < 6f)
            {
                _pathQueue.Dequeue();
                _pathTickCooldown = 0;
            }
            else
            {
                // Set facing direction
                if (Math.Abs(diff.X) > Math.Abs(diff.Y))
                    farmer.FacingDirection = diff.X > 0 ? 1 : 3;
                else
                    farmer.FacingDirection = diff.Y > 0 ? 2 : 0;

                var speed = farmer.getMovementSpeed();
                if (diff.Length() < speed)
                    farmer.Position = target;
                else
                {
                    diff.Normalize();
                    farmer.Position += diff * speed;
                }
            }
        }

        // Process command queue
        if (_commandQueue != null && _commandQueue.Count > 0 && Context.IsWorldReady)
        {
            // Wait for delay between commands
            if (_commandDelay > 0)
            {
                _commandDelay--;
                return;
            }

            // Wait for move to complete before next command
            if (_waitingForMove)
            {
                if (_pathQueue != null && _pathQueue.Count > 0)
                    return; // still walking
                _waitingForMove = false;
                _commandDelay = 5; // small gap after arriving
                return;
            }

            // Wait for fish bite
            if (_waitingForBite)
            {
                _biteTimeout--;
                if (_biteTimeout <= 0)
                {
                    _waitingForBite = false;
                    _commandResults.Add(new { ok = false, action = "wait_for_bite", error = "Timed out waiting for bite" });
                    // Don't abort queue - let next commands handle it
                }
                else if (Game1.player.CurrentTool is FishingRod fishRod && fishRod.isNibbling)
                {
                    _waitingForBite = false;
                    _commandResults.Add(new { ok = true, action = "wait_for_bite", message = "Fish is biting!" });
                    _commandDelay = 2; // tiny delay before reeling
                }
                else
                    return; // keep waiting
                return;
            }

            var cmd = _commandQueue.Dequeue();
            var action = cmd.ContainsKey("action") && cmd["action"] is JsonElement ae
                ? ae.GetString() ?? "" : "";

            try
            {
                switch (action)
                {
                    case "move":
                    {
                        var x = cmd.ContainsKey("x") && cmd["x"] is JsonElement xe ? xe.GetInt32() : 0;
                        var y = cmd.ContainsKey("y") && cmd["y"] is JsonElement ye ? ye.GetInt32() : 0;
                        var farmer = Game1.player;
                        var path = FindPath(farmer.currentLocation, farmer.TilePoint, new Point(x, y));
                        _pathQueue = path ?? new Queue<Point>(new[] { new Point(x, y) });
                        _pathTickCooldown = 0;
                        _waitingForMove = true;
                        _commandResults.Add(new { ok = true, action = "move", x, y });
                        break;
                    }
                    case "face":
                    {
                        var dir = cmd.ContainsKey("direction") && cmd["direction"] is JsonElement de ? de.GetInt32() : 2;
                        Game1.player.FacingDirection = dir;
                        _commandResults.Add(new { ok = true, action = "face", direction = dir });
                        _commandDelay = 3;
                        break;
                    }
                    case "select":
                    {
                        var name = cmd.ContainsKey("name") && cmd["name"] is JsonElement ne ? ne.GetString() ?? "" : "";
                        var farmer = Game1.player;
                        var idx = -1;
                        for (int i = 0; i < farmer.Items.Count; i++)
                        {
                            if (farmer.Items[i] != null && farmer.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            { idx = i; break; }
                        }
                        if (idx >= 0)
                        {
                            farmer.CurrentToolIndex = idx;
                            _commandResults.Add(new { ok = true, action = "select", name, slot = idx });
                        }
                        else
                            _commandResults.Add(new { ok = false, action = "select", error = $"Item '{name}' not found" });
                        _commandDelay = 3;
                        break;
                    }
                    case "use":
                    {
                        var farmer = Game1.player;
                        var item = farmer.CurrentItem;
                        if (item is Tool)
                        {
                            farmer.BeginUsingTool();
                            _commandResults.Add(new { ok = true, action = "use", item = item.Name });
                        }
                        else if (item is StardewValley.Object obj)
                        {
                            var facingTile = GetFacingTile(farmer);
                            int px = (int)facingTile.X * 64;
                            int py = (int)facingTile.Y * 64;
                            bool placed = obj.placementAction(farmer.currentLocation, px, py, farmer);
                            if (placed)
                            {
                                farmer.reduceActiveItemByOne();
                                _commandResults.Add(new { ok = true, action = "placed", item = item.Name });
                            }
                            else
                                _commandResults.Add(new { ok = false, action = "use", error = $"Cannot use '{item.Name}' here" });
                        }
                        else
                            _commandResults.Add(new { ok = false, action = "use", error = "No usable item" });
                        _commandDelay = 15; // tool animation time
                        break;
                    }
                    case "interact":
                    {
                        var farmer = Game1.player;
                        var facingTile = GetFacingTile(farmer);
                        var acted = farmer.currentLocation.checkAction(
                            new Location((int)facingTile.X, (int)facingTile.Y), Game1.viewport, farmer);
                        _commandResults.Add(new { ok = true, action = "interact", triggered = acted });
                        _commandDelay = 10;
                        break;
                    }
                    case "wait":
                    {
                        var ticks = cmd.ContainsKey("ticks") && cmd["ticks"] is JsonElement te ? te.GetInt32() : 60;
                        _commandResults.Add(new { ok = true, action = "wait", ticks });
                        _commandDelay = ticks;
                        break;
                    }
                    case "warp":
                    {
                        var loc = cmd.ContainsKey("location") && cmd["location"] is JsonElement le ? le.GetString() ?? "" : "";
                        var wx = cmd.ContainsKey("x") && cmd["x"] is JsonElement wxe ? wxe.GetInt32() : 10;
                        var wy = cmd.ContainsKey("y") && cmd["y"] is JsonElement wye ? wye.GetInt32() : 10;
                        Game1.warpFarmer(loc, wx, wy, false);
                        _commandResults.Add(new { ok = true, action = "warp", location = loc, x = wx, y = wy });
                        _commandDelay = 30; // wait for warp to complete
                        break;
                    }
                    case "wait_for_bite":
                    {
                        var timeout = cmd.ContainsKey("timeout") && cmd["timeout"] is JsonElement to ? to.GetInt32() : 1800;
                        _waitingForBite = true;
                        _biteTimeout = timeout;
                        break;
                    }
                    case "key":
                    {
                        var keyName = cmd.ContainsKey("key") && cmd["key"] is JsonElement ke ? ke.GetString() ?? "confirm" : "confirm";
                        switch (keyName.ToLower())
                        {
                            case "confirm": case "action":
                                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(), Game1.input.GetGamePadState());
                                break;
                            case "skip": case "escape":
                                if (Game1.activeClickableMenu != null)
                                    Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                                else
                                    Game1.activeClickableMenu?.exitThisMenu();
                                break;
                        }
                        _commandResults.Add(new { ok = true, action = "key", key = keyName });
                        _commandDelay = 10;
                        break;
                    }
                    default:
                        _commandResults.Add(new { ok = false, action, error = "Unknown action" });
                        break;
                }
            }
            catch (Exception ex)
            {
                _commandResults.Add(new { ok = false, action, error = ex.Message });
            }

            // All commands done? Return results
            if (_commandQueue.Count == 0)
            {
                _commandQueueTcs?.TrySetResult(new
                {
                    ok = true,
                    executed = _commandResults.Count,
                    results = _commandResults.ToArray()
                });
                _commandQueue = null;
            }
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            tcp.Stop();
            return true;
        }
        catch { return false; }
    }

    private void StartServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            // Auto-detect available port starting from 7842
            _listener = null;
            for (_port = 7842; _port < 7850; _port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://localhost:{_port}/");
                    listener.Start();
                    _listener = listener;
                    Monitor.Log($"NagiBridge HTTP server started on port {_port}", LogLevel.Info);
                    break;
                }
                catch
                {
                    Monitor.Log($"Port {_port} unavailable, trying next...", LogLevel.Debug);
                }
            }

            if (_listener == null)
            {
                Monitor.Log("Failed to start HTTP server on any port (7842-7849)", LogLevel.Error);
                return;
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(ctx), token);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Monitor.Log($"Listener error: {ex.Message}", LogLevel.Warn);
                }
            }
        }, token);
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        try
        {
            object? result = path switch
            {
                "/status" => HandleStatus(),
                "/move" => HandleMove(ctx),
                "/tool" => HandleTool(ctx),
                "/interact" => HandleInteract(ctx),
                "/chat" => HandleChat(ctx),
                "/emote" => HandleEmote(ctx),
                "/state" => HandleState(),
                "/surroundings" => HandleSurroundings(ctx),
                "/stop" => HandleStop(),
                "/map" => HandleMap(),
                "/buy" => HandleBuy(ctx),
                "/face" => HandleFace(ctx),
                "/select" => HandleSelect(ctx),
                "/use" => HandleUse(ctx),
                "/sleep" => HandleSleep(),
                "/wakeup" => HandleWakeup(),
                "/queue" => HandleQueue(ctx),
                "/key" => HandleKey(ctx),
                "/warp" => HandleWarp(ctx),
                "/pause" => HandlePause(),
                "/resume" => HandleResume(),
                "/give" => HandleGive(ctx),
                "/money" => HandleMoney(ctx),
                "/refill" => HandleRefill(),
                "/heal" => HandleHeal(),
                "/ripen" => HandleRipen(ctx),
                "/sell" => HandleSell(ctx),
                "/harvest" => HandleHarvest(ctx),
                "/store" => HandleStore(ctx),
                "/chest" => HandleChest(ctx),
                "/placechest" => HandlePlaceChest(ctx),
                "/fishbot" => HandleFishbot(ctx),
                "/minigame/state" => HandleMinigameState(),
                "/minigame/bot" => HandleMinigameBot(ctx),
                "/menu" => HandleMenu(),
                "/menu/click" => HandleMenuClick(ctx),
                "/craft" => HandleCraft(ctx),
                "/machines" => HandleMachines(),
                "/animals" => HandleAnimals(),
                _ => throw new InvalidOperationException($"Unknown endpoint: {path}")
            };

            Respond(ctx, 200, result ?? new { ok = true });
        }
        catch (Exception ex)
        {
            Respond(ctx, 400, new { error = ex.Message });
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, object body)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
        var buf = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }

    private Dictionary<string, object?> ReadJson(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body))
            return new Dictionary<string, object?>();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(body) ?? new();
    }

    private T GetParam<T>(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            throw new InvalidOperationException($"Missing parameter: {key}");

        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
            if (typeof(T) == typeof(float)) return (T)(object)je.GetSingle();
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
        }

        return (T)Convert.ChangeType(val, typeof(T));
    }

    private T GetParamOr<T>(Dictionary<string, object?> dict, string key, T defaultValue)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            return defaultValue;

        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
            if (typeof(T) == typeof(float)) return (T)(object)je.GetSingle();
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
        }

        return (T)Convert.ChangeType(val, typeof(T));
    }

    // --- Handlers ---

    private object HandleStatus()
    {
        return new
        {
            ok = true,
            server = "NagiBridge",
            version = "1.0.0",
            port = _port,
            worldReady = Context.IsWorldReady,
            isMultiplayer = Context.IsMultiplayer
        };
    }

    /// <summary>
    /// POST /move  { "x": 10, "y": 15 }
    /// Walks to tile (x, y) using simple straight-line pathfinding.
    /// </summary>
    private object HandleMove(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var tx = GetParam<int>(p, "x");
        var ty = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        // Build simple path: current tile -> target tile (straight line, then adjust)
        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var startTile = farmer.TilePoint;
            var path = FindPath(farmer.currentLocation, startTile, new Point(tx, ty));

            if (path == null || path.Count == 0)
            {
                // Fallback: just teleport-walk directly
                _pathQueue = new Queue<Point>();
                _pathQueue.Enqueue(new Point(tx, ty));
            }
            else
            {
                _pathQueue = path;
            }
            _pathTickCooldown = 0;

            tcs.SetResult(new { ok = true, message = $"Moving to ({tx},{ty}), steps={_pathQueue.Count}" });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /tool  { "name": "Axe" } or { "name": "current" }
    /// Swings the specified tool (or current tool) once.
    /// </summary>
    private object HandleTool(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var name = GetParamOr(p, "name", "current");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;

            if (name != "current")
            {
                var tool = farmer.Items
                    .Where(i => i is Tool)
                    .Cast<Tool>()
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (tool == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Tool '{name}' not found in inventory" });
                    return;
                }

                farmer.CurrentToolIndex = farmer.Items.IndexOf(tool);
            }

            farmer.BeginUsingTool();
            tcs.SetResult(new { ok = true, tool = farmer.CurrentTool?.Name ?? "none" });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /interact  { }
    /// Triggers an action check at the tile the farmer is facing.
    /// Returns what's on the tile for context.
    /// </summary>
    private object HandleInteract(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var facingTile = GetFacingTile(farmer);
            int ftx = (int)facingTile.X, fty = (int)facingTile.Y;
            var tileVec = new Vector2(ftx, fty);

            bool acted = loc.checkAction(
                new Location(ftx, fty),
                Game1.viewport,
                farmer
            );

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["actionTriggered"] = acted,
                ["facingTile"] = new { x = ftx, y = fty }
            };

            if (loc.objects.TryGetValue(tileVec, out var obj))
                result["object"] = obj.Name;
            if (loc.terrainFeatures.TryGetValue(tileVec, out var tf))
            {
                result["terrain"] = tf.GetType().Name;
                if (tf is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest())
                    result["harvestable"] = true;
            }
            var npc = loc.characters.FirstOrDefault(n => n.TilePoint.X == ftx && n.TilePoint.Y == fty);
            if (npc != null)
                result["npc"] = npc.Name;

            tcs.SetResult(result);
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /chat  { "message": "Hello!" }
    /// Sends a chat message visible to all players.
    /// </summary>
    private object HandleChat(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var message = GetParam<string>(p, "message");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        EnqueueMainThread(() =>
        {
            Game1.chatBox?.addMessage(message, Color.White);
            if (Context.IsMultiplayer)
            {
                Game1.chatBox?.setText(message);
                Game1.chatBox?.chatBox.RecieveCommandInput('\r');
            }
        });

        return new { ok = true, message };
    }

    /// <summary>
    /// POST /emote  { "id": 16 }
    /// Plays an emote animation on the farmer.
    /// Common emote IDs: 16=happy, 20=sad, 24=heart, 28=exclamation, 32=note, 36=sleep, 40=game, 52=angry, 56=laugh, 60=blush
    /// </summary>
    private object HandleEmote(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var id = GetParam<int>(p, "id");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        EnqueueMainThread(() =>
        {
            Game1.player.doEmote(id);
        });

        return new { ok = true, emoteId = id };
    }

    /// <summary>
    /// GET /state
    /// Returns comprehensive game state.
    /// </summary>
    private object HandleState()
    {
        if (!Context.IsWorldReady)
            return new { ok = true, worldReady = false };

        var farmer = Game1.player;
        var loc = farmer.currentLocation;

        var npcs = loc.characters
            .Select(n => new
            {
                name = n.Name,
                x = n.TilePoint.X,
                y = n.TilePoint.Y
            }).ToList();

        var inventory = farmer.Items
            .Where(i => i != null)
            .Select(i =>
            {
                var entry = new Dictionary<string, object?>
                {
                    ["name"] = i.Name,
                    ["stack"] = i.Stack,
                    ["category"] = i.getCategoryName()
                };
                if (i is WateringCan wc)
                {
                    entry["waterLeft"] = wc.WaterLeft;
                    entry["waterMax"] = wc.waterCanMax;
                }
                return entry;
            }).ToList();

        var menuInfo = (object?)null;
        if (Game1.activeClickableMenu != null)
        {
            var menuType = Game1.activeClickableMenu.GetType().Name;
            var dialogueText = "";
            if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox db)
            {
                try { dialogueText = db.getCurrentString() ?? ""; } catch { }
            }
            menuInfo = new
            {
                type = menuType,
                dialogue = string.IsNullOrEmpty(dialogueText) ? null : dialogueText
            };
        }

        var eventInfo = (object?)null;
        if (loc.currentEvent != null)
        {
            var ev = loc.currentEvent;
            string? evDialogue = null;
            if (Game1.activeClickableMenu is DialogueBox evDb)
            {
                try { evDialogue = evDb.getCurrentString(); } catch { }
            }
            eventInfo = new
            {
                id = ev.id,
                skippable = ev.skippable,
                message = evDialogue
            };
        }

        return new
        {
            ok = true,
            worldReady = true,
            player = new
            {
                name = farmer.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y,
                health = farmer.health,
                maxHealth = farmer.maxHealth,
                stamina = farmer.Stamina,
                maxStamina = farmer.MaxStamina,
                money = farmer.Money,
                currentTool = farmer.CurrentTool?.Name,
                facingDirection = farmer.FacingDirection,
                isMoving = _pathQueue != null && _pathQueue.Count > 0,
                fishing = farmer.CurrentTool is FishingRod rod ? new
                {
                    isCasting = rod.isTimingCast,
                    isFishing = rod.isFishing,
                    isNibbling = rod.isNibbling,
                    isReeling = rod.isReeling,
                    hit = rod.hit
                } : null
            },
            location = new
            {
                name = loc.Name,
                mapWidth = loc.Map.DisplayWidth / 64,
                mapHeight = loc.Map.DisplayHeight / 64
            },
            time = new
            {
                timeOfDay = Game1.timeOfDay,
                dayOfMonth = Game1.dayOfMonth,
                season = Game1.currentSeason,
                year = Game1.year
            },
            activeMenu = menuInfo,
            activeEvent = eventInfo,
            npcs,
            inventory
        };
    }

    /// <summary>
    /// GET /surroundings  ?radius=10
    /// Returns tile info around the player: passability, objects, terrain features, buildings, NPCs.
    /// </summary>
    private object HandleSurroundings(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 10;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 30)
            radius = r;

        var farmer = Game1.player;
        var loc = farmer.currentLocation;
        var cx = farmer.TilePoint.X;
        var cy = farmer.TilePoint.Y;

        var tiles = new List<object>();

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (tx < 0 || ty < 0) continue;
                var mapW = loc.Map.DisplayWidth / 64;
                var mapH = loc.Map.DisplayHeight / 64;
                if (tx >= mapW || ty >= mapH) continue;

                var tileVec = new Vector2(tx, ty);
                var passable = loc.isTilePassable(tileVec);

                string? objName = null;
                if (loc.objects.TryGetValue(tileVec, out var obj))
                    objName = obj.Name;

                string? terrainName = null;
                bool diggable = loc.doesTileHaveProperty(tx, ty, "Diggable", "Back") != null;
                bool watered = false;
                string? cropName = null;
                int cropPhase = -1;
                bool harvestable = false;

                if (loc.terrainFeatures.TryGetValue(tileVec, out var tf))
                {
                    terrainName = tf.GetType().Name;
                    if (tf is HoeDirt dirt)
                    {
                        terrainName = "HoeDirt";
                        watered = dirt.state.Value == 1;
                        if (dirt.crop != null)
                        {
                            cropName = dirt.crop.indexOfHarvest.Value;
                            cropPhase = dirt.crop.currentPhase.Value;
                            harvestable = dirt.readyForHarvest();
                        }
                    }
                    else if (tf is Tree tree)
                    {
                        terrainName = $"Tree:{tree.treeType.Value}";
                    }
                    else if (tf is GiantCrop gc)
                    {
                        terrainName = "GiantCrop";
                    }
                }

                string? resourceName = null;
                var clump = loc.resourceClumps.FirstOrDefault(c =>
                    c.Tile == tileVec || (tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value));
                if (clump != null)
                    resourceName = clump.parentSheetIndex.Value switch
                    {
                        600 => "LargeStump",
                        602 => "LargeLog",
                        622 => "MeteoriteOre",
                        672 => "LargeBoulder",
                        752 => "LargeBoulder",
                        754 => "LargeBoulder",
                        _ => $"Clump:{clump.parentSheetIndex.Value}"
                    };

                bool hasInfo = !passable || objName != null || terrainName != null
                    || resourceName != null || diggable || cropName != null;
                if (hasInfo)
                {
                    var tile = new Dictionary<string, object?> { ["x"] = tx, ["y"] = ty, ["passable"] = passable };
                    if (diggable) tile["diggable"] = true;
                    if (objName != null) tile["object"] = objName;
                    if (terrainName != null) tile["terrain"] = terrainName;
                    if (resourceName != null) tile["resource"] = resourceName;
                    if (cropName != null)
                    {
                        tile["crop"] = cropName;
                        tile["cropPhase"] = cropPhase;
                        tile["harvestable"] = harvestable;
                    }
                    if (watered) tile["watered"] = true;
                    tiles.Add(tile);
                }
            }
        }

        var nearbyNpcs = loc.characters
            .Where(n => !(n is Monster) && Math.Abs(n.TilePoint.X - cx) <= radius && Math.Abs(n.TilePoint.Y - cy) <= radius)
            .Select(n => new { name = n.Name, x = n.TilePoint.X, y = n.TilePoint.Y })
            .ToList();

        var nearbyMonsters = loc.characters
            .OfType<Monster>()
            .Where(m => Math.Abs(m.TilePoint.X - cx) <= radius && Math.Abs(m.TilePoint.Y - cy) <= radius)
            .Select(m => new { name = m.Name, x = m.TilePoint.X, y = m.TilePoint.Y, health = m.Health, maxHealth = m.MaxHealth })
            .ToList();

        var nearbyFarmers = Game1.getOnlineFarmers()
            .Where(f => f != farmer && f.currentLocation == loc
                && Math.Abs(f.TilePoint.X - cx) <= radius && Math.Abs(f.TilePoint.Y - cy) <= radius)
            .Select(f => new { name = f.Name, x = f.TilePoint.X, y = f.TilePoint.Y })
            .ToList();

        return new
        {
            ok = true,
            center = new { x = cx, y = cy },
            radius,
            location = loc.Name,
            tiles,
            npcs = nearbyNpcs,
            monsters = nearbyMonsters,
            farmers = nearbyFarmers
        };
    }

    /// <summary>
    /// POST /face  { "direction": 2 }
    /// Sets the farmer's facing direction. 0=up, 1=right, 2=down, 3=left
    /// </summary>
    private object HandleFace(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var dir = GetParam<int>(p, "direction");
        if (dir < 0 || dir > 3)
            throw new InvalidOperationException("direction must be 0-3 (up/right/down/left)");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            Game1.player.FacingDirection = dir;
            tcs.SetResult(new { ok = true, direction = dir });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /select  { "name": "Parsnip Seeds" }
    /// Selects an inventory item by name (sets it as the active toolbar slot).
    /// </summary>
    private object HandleSelect(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var name = GetParam<string>(p, "name");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var idx = -1;
            for (int i = 0; i < farmer.Items.Count; i++)
            {
                if (farmer.Items[i] != null &&
                    farmer.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                tcs.SetResult(new { ok = false, error = $"Item '{name}' not found in inventory" });
                return;
            }

            farmer.CurrentToolIndex = idx;
            tcs.SetResult(new { ok = true, selected = name, slot = idx });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /use  { "force": false }
    /// Uses the currently held item with pre-validation.
    /// Tools: checks if facing tile is appropriate (hoe→diggable empty, wateringcan→HoeDirt, axe→tree/stump, pickaxe→stone).
    /// Placeables: checks tile is clear. Pass force=true to skip validation.
    /// </summary>
    private object HandleUse(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var force = GetParamOr(p, "force", false);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var item = farmer.CurrentItem;
            if (item == null)
            {
                tcs.SetResult(new { ok = false, error = "No item selected" });
                return;
            }

            var facingTile = GetFacingTile(farmer);
            var loc = farmer.currentLocation;
            int ftx = (int)facingTile.X, fty = (int)facingTile.Y;
            var tileVec = new Vector2(ftx, fty);

            if (item is Tool tool && !force)
            {
                var validation = ValidateToolUse(tool, loc, tileVec, ftx, fty);
                if (validation != null)
                {
                    tcs.SetResult(new { ok = false, error = validation,
                        tile = new { x = ftx, y = fty }, tool = tool.Name });
                    return;
                }
            }

            if (item is Tool)
            {
                farmer.BeginUsingTool();
                tcs.SetResult(new { ok = true, action = "tool", item = item.Name,
                    tile = new { x = ftx, y = fty } });
            }
            else if (item is StardewValley.Object obj)
            {
                int px = ftx * 64, py = fty * 64;
                bool placed = obj.placementAction(loc, px, py, farmer);
                if (placed)
                {
                    farmer.reduceActiveItemByOne();
                    tcs.SetResult(new { ok = true, action = "placed", item = item.Name,
                        tile = new { x = ftx, y = fty } });
                }
                else
                {
                    tcs.SetResult(new { ok = false, error = $"Cannot place '{item.Name}' here",
                        tile = new { x = ftx, y = fty } });
                }
            }
            else
            {
                tcs.SetResult(new { ok = false, error = $"Cannot use '{item.Name}' (unsupported item type)" });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private string? ValidateToolUse(Tool tool, GameLocation loc, Vector2 tileVec, int tx, int ty)
    {
        bool hasObj = loc.objects.ContainsKey(tileVec);
        loc.terrainFeatures.TryGetValue(tileVec, out var tf);
        bool diggable = loc.doesTileHaveProperty(tx, ty, "Diggable", "Back") != null;

        switch (tool)
        {
            case Hoe:
                if (tf is HoeDirt)
                    return "Tile already tilled";
                if (hasObj)
                    return $"Tile blocked by object: {loc.objects[tileVec].Name}";
                if (!diggable)
                    return "Tile is not diggable";
                return null;

            case WateringCan:
                if (tf is not HoeDirt dirt)
                    return "No tilled soil here — till first";
                if (dirt.state.Value == 1)
                    return "Already watered";
                return null;

            case Axe:
                bool hasTree = tf is Tree;
                bool hasStump = loc.resourceClumps.Any(c =>
                    (c.parentSheetIndex.Value == 600 || c.parentSheetIndex.Value == 602)
                    && tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value);
                bool hasTwig = hasObj && loc.objects[tileVec].Name == "Twig";
                if (!hasTree && !hasStump && !hasTwig)
                    return "Nothing to chop here";
                return null;

            case Pickaxe:
                bool hasStone = hasObj && loc.objects[tileVec].Name == "Stone";
                bool hasBoulder = loc.resourceClumps.Any(c =>
                    (c.parentSheetIndex.Value == 672 || c.parentSheetIndex.Value == 752 || c.parentSheetIndex.Value == 754 || c.parentSheetIndex.Value == 622)
                    && tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value);
                if (!hasStone && !hasBoulder && tf is not HoeDirt)
                    return "Nothing to break here";
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// GET /map
    /// Returns buildings, warps, NPCs, and other farmers for the current location.
    /// Provides everything needed for long-range pathfinding and navigation.
    /// </summary>
    private object HandleMap()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var mapWidth = loc.Map.DisplayWidth / 64;
            var mapHeight = loc.Map.DisplayHeight / 64;

            // Buildings (Farm, etc.)
            var buildings = new List<object>();
            if (loc is Farm farm)
            {
                foreach (var b in farm.buildings)
                {
                    var entry = new Dictionary<string, object?>
                    {
                        ["type"] = b.buildingType.Value,
                        ["x"] = b.tileX.Value,
                        ["y"] = b.tileY.Value,
                        ["width"] = b.tilesWide.Value,
                        ["height"] = b.tilesHigh.Value
                    };
                    if (b.humanDoor.Value != Point.Zero || b.humanDoor.Value != default)
                    {
                        entry["doorX"] = b.tileX.Value + b.humanDoor.X;
                        entry["doorY"] = b.tileY.Value + b.humanDoor.Y;
                    }
                    buildings.Add(entry);
                }
            }

            // Warps (exits/entrances to other maps)
            var warps = loc.warps
                .Select(w => new
                {
                    x = w.X,
                    y = w.Y,
                    targetLocation = w.TargetName,
                    targetX = w.TargetX,
                    targetY = w.TargetY
                }).ToList();

            // All NPCs in current location
            var npcs = loc.characters
                .Select(n => new
                {
                    name = n.Name,
                    x = n.TilePoint.X,
                    y = n.TilePoint.Y
                }).ToList();

            // All other farmers in current location
            var farmers = Game1.getOnlineFarmers()
                .Where(f => f != farmer && f.currentLocation == loc)
                .Select(f => new
                {
                    name = f.Name,
                    x = f.TilePoint.X,
                    y = f.TilePoint.Y
                }).ToList();

            // Animals (if on farm or animal building interior)
            var animals = new List<object>();
            if (loc is Farm farmLoc)
            {
                foreach (var a in farmLoc.animals.Values)
                    animals.Add(new { name = a.Name, type = a.type.Value, x = a.TilePoint.X, y = a.TilePoint.Y });
            }
            else if (loc is AnimalHouse ah)
            {
                foreach (var a in ah.animals.Values)
                    animals.Add(new { name = a.Name, type = a.type.Value, x = a.TilePoint.X, y = a.TilePoint.Y });
            }

            tcs.SetResult(new
            {
                ok = true,
                player = new { x = farmer.TilePoint.X, y = farmer.TilePoint.Y },
                location = new
                {
                    name = loc.Name,
                    width = mapWidth,
                    height = mapHeight
                },
                buildings,
                warps,
                npcs,
                farmers,
                animals
            });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /buy  { "id": "472", "quantity": 5 }  or  { "id": "(O)472", "quantity": 5 }
    /// Buys an item: deducts gold, adds item to inventory.
    /// Optional "price" param to override per-unit cost; otherwise uses the item's default sale price * 2 (shop markup).
    /// </summary>
    private object HandleBuy(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var rawId = GetParam<string>(p, "id");
        var quantity = GetParamOr(p, "quantity", 1);
        var priceOverride = GetParamOr(p, "price", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                // Qualify the item ID if needed (e.g. "472" -> "(O)472")
                var qualifiedId = rawId.StartsWith("(") ? rawId : ItemRegistry.QualifyItemId(rawId);
                if (qualifiedId == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Unknown item ID: {rawId}" });
                    return;
                }

                // Create a test item to get its info
                var testItem = ItemRegistry.Create(qualifiedId, 1);
                if (testItem == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Cannot create item: {qualifiedId}" });
                    return;
                }

                // Calculate price: override > default (salePrice * 2 as shop markup)
                int unitPrice = priceOverride >= 0
                    ? priceOverride
                    : (testItem is StardewValley.Object obj ? obj.salePrice() * 2 : 100);
                int totalCost = unitPrice * quantity;

                var farmer = Game1.player;
                if (farmer.Money < totalCost)
                {
                    tcs.SetResult(new { ok = false, error = $"Not enough gold. Need {totalCost}g, have {farmer.Money}g",
                        need = totalCost, have = farmer.Money });
                    return;
                }

                // Create the actual item and add to inventory
                var item = ItemRegistry.Create(qualifiedId, quantity);
                farmer.Money -= totalCost;
                farmer.addItemByMenuIfNecessary(item);

                tcs.SetResult(new
                {
                    ok = true,
                    bought = item.Name,
                    quantity,
                    unitPrice,
                    totalCost,
                    remainingGold = farmer.Money
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /sleep
    /// Warps the farmer to their bed and triggers sleep (end of day).
    /// </summary>
    private object HandleSleep()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;

                // Find home: try homeLocation, then scan all locations for a cabin belonging to this farmer
                var homeName = farmer.homeLocation.Value;
                GameLocation homeLoc = null;
                if (!string.IsNullOrEmpty(homeName))
                    homeLoc = Game1.getLocationFromName(homeName);

                if (homeLoc == null)
                {
                    // Scan for cabin with this farmer's unique ID
                    foreach (var loc in Game1.locations)
                    {
                        if (loc is StardewValley.Locations.Cabin cabin && cabin.owner == farmer)
                        {
                            homeLoc = cabin;
                            homeName = cabin.Name;
                            break;
                        }
                    }
                }

                // Fallback to FarmHouse for host
                if (homeLoc == null)
                {
                    homeLoc = Game1.getLocationFromName("FarmHouse");
                    homeName = "FarmHouse";
                }

                if (homeLoc == null)
                {
                    tcs.SetResult(new { ok = false, error = "Cannot find home location" });
                    return;
                }

                var bedX = 10;
                var bedY = 6;

                var needsWarp = farmer.currentLocation.Name != homeLoc.Name;
                if (needsWarp)
                {
                    Game1.warpFarmer(homeName, bedX, bedY, false);
                }

                // Longer delay for farmhand warp sync
                var delay = needsWarp ? 3000 : 500;
                DelayedAction.functionAfterDelay(() =>
                {
                    var f = Game1.player;
                    f.isInBed.Value = true;
                    f.sleptInTemporaryBed.Value = false;
                    f.currentLocation.answerDialogueAction("Sleep_Yes", Array.Empty<string>());

                    DelayedAction.functionAfterDelay(() =>
                    {
                        if (Game1.activeClickableMenu != null)
                        {
                            Game1.player.currentLocation.answerDialogueAction("Sleep_Yes", Array.Empty<string>());
                            Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(),
                                Game1.input.GetGamePadState());
                        }
                    }, 1000);
                }, delay);

                tcs.SetResult(new { ok = true, action = "sleeping", home = homeName, bed = $"{bedX},{bedY}" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /wakeup
    /// After sleeping / new day, walks the farmer out of their cabin to the farm.
    /// Returns current location and position.
    /// </summary>
    private object HandleWakeup()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;

            // Find any warp out of current indoor location
            var warp = loc.warps.FirstOrDefault();
            if (warp != null)
            {
                // Directly warp the farmer - more reliable than walking
                Game1.warpFarmer(warp.TargetName, warp.TargetX, warp.TargetY, false);
                tcs.SetResult(new
                {
                    ok = true,
                    action = "warped",
                    from = loc.Name,
                    target = warp.TargetName,
                    x = warp.TargetX,
                    y = warp.TargetY
                });
            }
            else
            {
                tcs.SetResult(new { ok = true, action = "already_outside", location = loc.Name,
                    x = farmer.TilePoint.X, y = farmer.TilePoint.Y });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /stop
    /// Cancels current movement.
    /// </summary>
    /// <summary>
    /// POST /queue  [{"action":"move","x":60,"y":17},{"action":"select","name":"Hoe"},{"action":"face","direction":2},{"action":"use"},...]
    /// Executes a sequence of commands automatically. Supported actions: move, face, select, use, interact, wait.
    /// Returns all results when the queue finishes.
    /// <summary>
    /// POST /key  { "key": "confirm" }
    /// Simulates a key press. Used to advance dialogue, confirm menus, skip cutscenes.
    /// Supported keys: confirm (action button), cancel (back/menu), skip (escape)
    /// </summary>
    /// <summary>
    /// POST /warp  { "location": "Beach", "x": 20, "y": 4 }
    /// Teleports the farmer to any game location. If x/y omitted, warps to default entry point.
    /// Common locations: Farm, Town, Beach, Mountain, Forest, Mine, BusStop, Desert, FishShop
    /// </summary>
    private object HandleWarp(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var location = GetParam<string>(p, "location");
        var x = GetParamOr(p, "x", -1);
        var y = GetParamOr(p, "y", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var targetLoc = Game1.getLocationFromName(location);
                if (targetLoc == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Location '{location}' not found" });
                    return;
                }

                // If no coordinates given, try to find a reasonable entry point
                if (x < 0 || y < 0)
                {
                    // Use the first warp that targets this location from current map, or default center
                    var farmer = Game1.player;
                    var curWarps = farmer.currentLocation.warps;
                    var matchWarp = curWarps.FirstOrDefault(w => w.TargetName == location);
                    if (matchWarp != null)
                    {
                        Game1.warpFarmer(location, matchWarp.TargetX, matchWarp.TargetY, false);
                    }
                    else
                    {
                        // Default: warp to center-ish of map
                        var mw = targetLoc.Map.DisplayWidth / 64;
                        var mh = targetLoc.Map.DisplayHeight / 64;
                        Game1.warpFarmer(location, mw / 2, mh / 2, false);
                    }
                }
                else
                {
                    Game1.warpFarmer(location, x, y, false);
                }

                tcs.SetResult(new { ok = true, action = "warped", location, x, y });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleKey(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var key = GetParamOr(p, "key", "confirm");
        var count = GetParamOr(p, "count", 1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    switch (key.ToLower())
                    {
                        case "confirm":
                        case "action":
                            if (Game1.activeClickableMenu is DialogueBox dialogueBox)
                            {
                                dialogueBox.receiveLeftClick(0, 0);
                            }
                            else if (Game1.activeClickableMenu != null)
                            {
                                Game1.activeClickableMenu.receiveLeftClick(
                                    Game1.activeClickableMenu.xPositionOnScreen + Game1.activeClickableMenu.width / 2,
                                    Game1.activeClickableMenu.yPositionOnScreen + Game1.activeClickableMenu.height / 2);
                            }
                            else if (Game1.currentLocation?.currentEvent != null)
                            {
                                Game1.currentLocation.currentEvent.receiveActionPress(0, 0);
                            }
                            else if (Game1.input != null)
                            {
                                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(),
                                    Game1.input.GetGamePadState());
                            }
                            break;
                        case "ok":
                            if (Game1.activeClickableMenu != null)
                            {
                                var okBtn = Game1.activeClickableMenu.GetType()
                                    .GetField("okButton", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?
                                    .GetValue(Game1.activeClickableMenu) as ClickableTextureComponent;
                                if (okBtn != null)
                                {
                                    Game1.activeClickableMenu.receiveLeftClick(
                                        okBtn.bounds.Center.X, okBtn.bounds.Center.Y);
                                }
                                else
                                {
                                    Game1.activeClickableMenu.exitThisMenu();
                                }
                            }
                            break;
                        case "cancel":
                        case "back":
                        case "menu":
                            if (Game1.activeClickableMenu != null)
                                Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            else if (Game1.input != null)
                                Game1.pressUseToolButton();
                            break;
                        case "skip":
                        case "escape":
                            if (Game1.currentLocation?.currentEvent != null)
                            {
                                Game1.currentLocation.currentEvent.skipped = true;
                                Game1.currentLocation.currentEvent.skipEvent();
                            }
                            else
                            {
                                Game1.currentMinigame?.receiveKeyPress(Keys.Escape);
                                if (Game1.activeClickableMenu != null)
                                    Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            }
                            break;
                        default:
                            if (key.ToLower().StartsWith("f") && int.TryParse(key.Substring(1), out int fNum) && fNum >= 1 && fNum <= 12)
                            {
                                byte vk = (byte)(0x70 + fNum - 1); // VK_F1=0x70
                                keybd_event(vk, 0, 0, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(50);
                                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                            break;
                    }
                }
                tcs.SetResult(new { ok = true, key, count });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// </summary>
    private object HandleQueue(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Empty command queue");

        var commands = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(body);
        if (commands == null || commands.Count == 0)
            throw new InvalidOperationException("No commands in queue");

        _commandQueueTcs = new TaskCompletionSource<object>();
        _commandResults.Clear();

        EnqueueMainThread(() =>
        {
            _commandQueue = new Queue<Dictionary<string, object?>>(commands);
            _commandDelay = 0;
            _waitingForMove = false;
        });

        // Wait for all commands to execute (timeout 5 minutes)
        if (_commandQueueTcs.Task.Wait(TimeSpan.FromMinutes(5)))
            return _commandQueueTcs.Task.Result;
        else
            return new { ok = false, error = "Queue execution timed out", executed = _commandResults.Count };
    }

    private object HandleStop()
    {
        _pathQueue = null;
        return new { ok = true, message = "Movement stopped" };
    }

    private object HandlePlaceChest(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var loc = Game1.player.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (loc.objects.ContainsKey(tileVec))
            {
                tcs.SetResult(new { ok = false, error = $"Tile ({cx},{cy}) already has an object" });
                return;
            }

            var chest = new StardewValley.Objects.Chest(true, tileVec);
            loc.objects.Add(tileVec, chest);
            tcs.SetResult(new { ok = true, placed = "Chest", x = cx, y = cy });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleStore(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");
        var name = GetParamOr(p, "name", "");
        var keepTools = GetParamOr(p, "keepTools", true);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (!loc.objects.TryGetValue(tileVec, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                tcs.SetResult(new { ok = false, error = $"No chest at ({cx},{cy})" });
                return;
            }

            var stored = new List<object>();
            for (int i = farmer.Items.Count - 1; i >= 0; i--)
            {
                var item = farmer.Items[i];
                if (item == null) continue;
                if (keepTools && item is Tool) continue;
                if (!string.IsNullOrEmpty(name)
                    && !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var leftover = chest.addItem(item);
                if (leftover == null)
                {
                    stored.Add(new { item = item.Name, count = item.Stack });
                    farmer.Items[i] = null;
                }
                else if (leftover.Stack < item.Stack)
                {
                    stored.Add(new { item = item.Name, count = item.Stack - leftover.Stack });
                    farmer.Items[i] = leftover;
                }
            }

            tcs.SetResult(new { ok = true, stored, chestAt = new { x = cx, y = cy } });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleChest(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (!loc.objects.TryGetValue(tileVec, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                tcs.SetResult(new { ok = false, error = $"No chest at ({cx},{cy})" });
                return;
            }

            var items = chest.Items
                .Where(i => i != null)
                .Select(i => new { name = i.Name, count = i.Stack })
                .ToList();

            tcs.SetResult(new { ok = true, items, capacity = chest.GetActualCapacity(), used = items.Count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleHarvest(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 15;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 50)
            radius = r;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            int count = 0;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest())
                {
                    var pos = pair.Key;
                    if (Math.Abs(pos.X - farmer.TilePoint.X) > radius
                        || Math.Abs(pos.Y - farmer.TilePoint.Y) > radius)
                        continue;

                    if (dirt.crop.harvest((int)pos.X, (int)pos.Y, dirt))
                    {
                        dirt.destroyCrop(false);
                        count++;
                    }
                }
            }

            tcs.SetResult(new { ok = true, harvested = count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleSell(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var name = GetParamOr(p, "name", "");
        var sellAll = GetParamOr(p, "all", false);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;

            var bin = loc is Farm farm
                ? farm.getShippingBin(farmer)
                : null;

            if (bin == null)
            {
                tcs.SetResult(new { ok = false, error = "No shipping bin found (must be on Farm)" });
                return;
            }

            var sold = new List<object>();
            var keepCategories = new HashSet<int> { -99, -98, -97, -96 }; // tools, rings, boots, weapons

            for (int i = farmer.Items.Count - 1; i >= 0; i--)
            {
                var item = farmer.Items[i];
                if (item == null) continue;
                if (item is Tool) continue;
                if (keepCategories.Contains(item.Category)) continue;

                if (!sellAll && !string.IsNullOrEmpty(name)
                    && !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (sellAll && item.Name.Contains("Seeds", StringComparison.OrdinalIgnoreCase))
                    continue;

                var salePrice = item is StardewValley.Object obj ? obj.sellToStorePrice() * item.Stack : 0;
                sold.Add(new { item = item.Name, count = item.Stack, price = salePrice });

                bin.Add(item);
                farmer.Items[i] = null;
            }

            tcs.SetResult(new { ok = true, sold, totalItems = sold.Count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleRefill()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var wc = Game1.player.Items.OfType<WateringCan>().FirstOrDefault();
            if (wc == null)
            {
                tcs.SetResult(new { ok = false, error = "No watering can in inventory" });
                return;
            }
            wc.WaterLeft = wc.waterCanMax;
            tcs.SetResult(new { ok = true, water = wc.WaterLeft, max = wc.waterCanMax });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleHeal()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var f = Game1.player;
            f.health = f.maxHealth;
            f.Stamina = f.MaxStamina;
            tcs.SetResult(new { ok = true, health = f.health, stamina = f.Stamina });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleRipen(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 30;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 50)
            radius = r;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            int count = 0;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && !dirt.readyForHarvest())
                {
                    var pos = pair.Key;
                    if (Math.Abs(pos.X - farmer.TilePoint.X) <= radius
                        && Math.Abs(pos.Y - farmer.TilePoint.Y) <= radius)
                    {
                        dirt.crop.growCompletely();
                        count++;
                    }
                }
            }

            tcs.SetResult(new { ok = true, ripened = count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleGive(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var itemId = GetParam<string>(p, "id");
        var count = GetParamOr(p, "count", 1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var item = ItemRegistry.Create(itemId, count);
            farmer.addItemToInventory(item);
            tcs.SetResult(new { ok = true, given = item.Name, count, id = itemId });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMoney(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var amount = GetParam<int>(p, "amount");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            Game1.player.Money += amount;
            tcs.SetResult(new { ok = true, added = amount, total = Game1.player.Money });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandlePause()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");
        _frozenTime = Game1.timeOfDay;
        _timeFrozen = true;
        return new { ok = true, action = "paused", frozenAt = _frozenTime };
    }

    private object HandleResume()
    {
        _timeFrozen = false;
        return new { ok = true, action = "resumed" };
    }

    private object HandleFishbot(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var action = GetParamOr(p, "action", "toggle"); // on, off, toggle, status

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                // Find Fishbot mod via SMAPI mod registry
                object? fishbotMod = null;
                System.Reflection.FieldInfo? autoField = null;

                var modInfo = this.Helper.ModRegistry.Get("AdroSlice.Fishbot");
                if (modInfo != null)
                {
                    var modInfoType = modInfo.GetType();
                    var modProp = modInfoType.GetProperty("Mod",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    fishbotMod = modProp?.GetValue(modInfo);
                    if (fishbotMod == null)
                    {
                        var modField = modInfoType.GetField("Mod",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        fishbotMod = modField?.GetValue(modInfo);
                    }
                }

                if (fishbotMod == null)
                {
                    tcs.SetResult(new { ok = false, error = "Fishbot mod not found" });
                    return;
                }

                // Find AutomationEnabled field/property
                var fbType = fishbotMod.GetType();
                autoField = fbType.GetField("AutomationEnabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

                var autoProp = fbType.GetProperty("AutomationEnabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

                var bindingAll = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

                if (autoField != null || autoProp != null)
                {
                    bool current = autoField != null
                        ? (bool)autoField.GetValue(fishbotMod)!
                        : (bool)autoProp!.GetValue(fishbotMod)!;
                    bool target = action == "toggle" ? !current : action == "on";

                    if (action != "status")
                    {
                        if (autoField != null) autoField.SetValue(fishbotMod, target);
                        else autoProp!.SetValue(fishbotMod, target);

                        if (target)
                        {
                            var startMethod = fbType.GetMethod("StartCasting", bindingAll);
                            startMethod?.Invoke(fishbotMod, null);
                        }
                        else
                        {
                            var resetMethod = fbType.GetMethod("reset", bindingAll)
                                ?? fbType.GetMethod("Reset", bindingAll);
                            resetMethod?.Invoke(fishbotMod, null);
                        }
                    }
                    tcs.SetResult(new { ok = true, enabled = action == "status" ? current : target });
                }
                else
                {
                    // List all fields for debugging
                    var fields = fbType.GetFields(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                    var names = string.Join(", ", fields.Select(f => f.Name));
                    tcs.SetResult(new { ok = false, error = $"AutomationEnabled not found. Fields: {names}" });
                }
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMinigameState()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                tcs.SetResult(BuildPrairieKingState());
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMinigameBot(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var action = GetParamOr(p, "action", "status").ToLowerInvariant();

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var minigame = Game1.currentMinigame;
                var inPrairieKing = IsPrairieKing(minigame);

                switch (action)
                {
                    case "start":
                        _minigameBotActive = true;
                        _minigameBotLastError = null;
                        break;
                    case "stop":
                        _minigameBotActive = false;
                        if (inPrairieKing)
                            ClearPrairieKingInput(minigame!);
                        break;
                    case "status":
                        break;
                    default:
                        tcs.SetResult(new { ok = false, error = "action must be start, stop, or status" });
                        return;
                }

                tcs.SetResult(new
                {
                    ok = true,
                    active = _minigameBotActive,
                    inPrairieKing,
                    currentMinigame = minigame?.GetType().FullName,
                    lastMove = new { x = _minigameBotLastMove.X, y = _minigameBotLastMove.Y },
                    lastError = _minigameBotLastError
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object BuildPrairieKingState()
    {
        var minigame = Game1.currentMinigame;
        if (!IsPrairieKing(minigame))
        {
            return new
            {
                ok = true,
                active = false,
                botActive = _minigameBotActive,
                currentMinigame = minigame?.GetType().FullName
            };
        }

        EnsurePrairieKingReflection(minigame!);
        var playerBounds = ReadPrairieKingPlayerBounds(minigame!);
        var monsters = ReadPrairieKingMonsters(minigame!);
        var bullets = ReadPrairieKingBullets(minigame!);
        var powerups = ReadPrairieKingPowerups(minigame!);

        return new
        {
            ok = true,
            active = true,
            botActive = _minigameBotActive,
            currentMinigame = minigame!.GetType().FullName,
            flags = new
            {
                shopping = IsPrairieKingShopping(minigame),
                death = IsPrairieKingDeathState(minigame),
                betweenWaves = IsPrairieKingBetweenWaves(minigame, monsters.Count),
                gameOver = ReadBoolField(minigame, _pkGameOverField)
            },
            player = new
            {
                x = playerBounds.Center.X,
                y = playerBounds.Center.Y,
                bounds = new { playerBounds.X, playerBounds.Y, playerBounds.Width, playerBounds.Height },
                lives = ReadIntField(minigame, _pkLivesField, -1),
                coins = ReadIntField(minigame, _pkCoinsField, -1),
                wave = ReadIntField(minigame, _pkWhichWaveField, -1)
            },
            counts = new { monsters = monsters.Count, bullets = bullets.Count, powerups = powerups.Count },
            monsters = monsters.Select(m => new
            {
                x = m.Bounds.Center.X,
                y = m.Bounds.Center.Y,
                bounds = new { m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height },
                m.Health,
                m.Type,
                m.Speed
            }).ToList(),
            bullets = bullets.Select(b => new
            {
                x = b.Bounds.Center.X,
                y = b.Bounds.Center.Y,
                bounds = new { b.Bounds.X, b.Bounds.Y, b.Bounds.Width, b.Bounds.Height },
                motion = new { x = b.Motion.X, y = b.Motion.Y },
                b.Damage
            }).ToList(),
            powerups = powerups.Select(p => new
            {
                x = p.Bounds.Center.X,
                y = p.Bounds.Center.Y,
                bounds = new { p.Bounds.X, p.Bounds.Y, p.Bounds.Width, p.Bounds.Height },
                p.Which
            }).ToList(),
            inputFields = new
            {
                movement = _pkPlayerMovementDirectionsField?.Name,
                shooting = _pkPlayerShootingDirectionsField?.Name
            },
            lastMove = new { x = _minigameBotLastMove.X, y = _minigameBotLastMove.Y },
            lastError = _minigameBotLastError
        };
    }

    private void TickPrairieKingBot(object game)
    {
        EnsurePrairieKingReflection(game);

        if (IsPrairieKingShopping(game) || IsPrairieKingDeathState(game))
        {
            ClearPrairieKingInput(game);
            return;
        }

        var playerBounds = ReadPrairieKingPlayerBounds(game);
        if (playerBounds.Width <= 0 || playerBounds.Height <= 0)
        {
            ClearPrairieKingInput(game);
            return;
        }

        var monsters = ReadPrairieKingMonsters(game);
        var bullets = ReadPrairieKingBullets(game);
        var powerups = ReadPrairieKingPowerups(game);

        if (IsPrairieKingBetweenWaves(game, monsters.Count) && powerups.Count == 0)
        {
            ClearPrairieKingInput(game);
            return;
        }

        var player = new Vector2(playerBounds.Center.X, playerBounds.Center.Y);
        var move = ChoosePrairieKingMove(player, monsters, bullets, powerups);
        var moveDirections = DirectionsFromVector(move, 0.25f);
        SetPrairieKingDirections(game, _pkPlayerMovementDirectionsField, moveDirections);
        _minigameBotLastMove = move;

        var target = monsters
            .Where(m => m.Health != 0)
            .OrderBy(m => Vector2.DistanceSquared(player, new Vector2(m.Bounds.Center.X, m.Bounds.Center.Y)))
            .FirstOrDefault();

        if (target == null)
        {
            SetPrairieKingDirections(game, _pkPlayerShootingDirectionsField, Array.Empty<int>());
            return;
        }

        var shootVector = new Vector2(target.Bounds.Center.X - player.X, target.Bounds.Center.Y - player.Y);
        SetPrairieKingDirections(game, _pkPlayerShootingDirectionsField, DirectionsFromVector(shootVector, 0.15f));
        _minigameBotLastError = null;
    }

    private static Vector2 ChoosePrairieKingMove(
        Vector2 player,
        List<PrairieKingMonster> monsters,
        List<PrairieKingBullet> bullets,
        List<PrairieKingPowerup> powerups)
    {
        var candidates = new[]
        {
            Vector2.Zero,
            new Vector2(0, -1),
            new Vector2(1, -1),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1),
            new Vector2(-1, 1),
            new Vector2(-1, 0),
            new Vector2(-1, -1)
        };

        var best = Vector2.Zero;
        var bestScore = double.NegativeInfinity;

        foreach (var raw in candidates)
        {
            var move = raw;
            if (move != Vector2.Zero)
                move.Normalize();

            var probe = player + move * 42f;
            var score = 0.0;

            if (probe.X < 48 || probe.X > 720 || probe.Y < 48 || probe.Y > 720)
                score -= 20000;

            var center = new Vector2(384, 384);
            score -= Vector2.Distance(probe, center) * 0.03;

            foreach (var bullet in bullets)
            {
                var bulletPos = new Vector2(bullet.Bounds.Center.X, bullet.Bounds.Center.Y);
                var motion = new Vector2(bullet.Motion.X, bullet.Motion.Y);
                var worst = double.PositiveInfinity;

                for (int i = 0; i <= 18; i += 3)
                {
                    var future = bulletPos + motion * i;
                    worst = Math.Min(worst, Vector2.Distance(probe, future));
                }

                if (worst < 34)
                    score -= 50000;
                else if (worst < 96)
                    score -= (96 - worst) * 45;

                var towardPlayer = player - bulletPos;
                if (motion != Vector2.Zero && towardPlayer != Vector2.Zero)
                {
                    motion.Normalize();
                    towardPlayer.Normalize();
                    score -= Math.Max(0, Vector2.Dot(motion, towardPlayer)) * 350;
                }
            }

            foreach (var monster in monsters)
            {
                var monsterPos = new Vector2(monster.Bounds.Center.X, monster.Bounds.Center.Y);
                var dist = Vector2.Distance(probe, monsterPos);
                if (dist < 58)
                    score -= 35000;
                else if (dist < 150)
                    score -= (150 - dist) * 85;
                else if (dist > 230)
                    score += Math.Min(90, (dist - 230) * 0.2);
            }

            foreach (var powerup in powerups)
            {
                var powerupPos = new Vector2(powerup.Bounds.Center.X, powerup.Bounds.Center.Y);
                var powerupDistance = Vector2.Distance(probe, powerupPos);
                var nearestMonster = monsters.Count == 0
                    ? double.PositiveInfinity
                    : monsters.Min(m => Vector2.Distance(powerupPos, new Vector2(m.Bounds.Center.X, m.Bounds.Center.Y)));
                var nearestBullet = bullets.Count == 0
                    ? double.PositiveInfinity
                    : bullets.Min(b => Vector2.Distance(powerupPos, new Vector2(b.Bounds.Center.X, b.Bounds.Center.Y)));

                if (nearestMonster > 115 && nearestBullet > 80)
                    score += 1600 / Math.Max(1, powerupDistance);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = move;
            }
        }

        return best;
    }

    private static void ClearPrairieKingInput(object game)
    {
        EnsurePrairieKingReflection(game);
        SetPrairieKingDirections(game, _pkPlayerMovementDirectionsField, Array.Empty<int>());
        SetPrairieKingDirections(game, _pkPlayerShootingDirectionsField, Array.Empty<int>());
    }

    private static void SetPrairieKingDirections(object game, FieldInfo? field, IEnumerable<int> directions)
    {
        if (field == null)
            return;

        var dirArray = directions.Distinct().ToArray();
        var target = field.IsStatic ? null : game;
        var current = field.GetValue(target);

        if (current is ICollection<int> intCollection)
        {
            intCollection.Clear();
            foreach (var direction in dirArray)
                intCollection.Add(direction);
            return;
        }

        if (field.FieldType == typeof(int[]))
        {
            field.SetValue(target, dirArray);
            return;
        }

        if (field.FieldType.IsAssignableFrom(typeof(List<int>)))
            field.SetValue(target, dirArray.ToList());
    }

    private static int[] DirectionsFromVector(Vector2 vector, float threshold)
    {
        if (vector == Vector2.Zero)
            return Array.Empty<int>();

        if (vector.LengthSquared() > 1f)
            vector.Normalize();

        var directions = new List<int>(2);
        if (vector.Y < -threshold)
            directions.Add(0);
        if (vector.X > threshold)
            directions.Add(1);
        if (vector.Y > threshold)
            directions.Add(2);
        if (vector.X < -threshold)
            directions.Add(3);

        if (directions.Count > 0)
            return directions.ToArray();

        return Math.Abs(vector.X) > Math.Abs(vector.Y)
            ? new[] { vector.X >= 0 ? 1 : 3 }
            : new[] { vector.Y >= 0 ? 2 : 0 };
    }

    private static bool IsPrairieKing(object? minigame)
    {
        if (minigame == null)
            return false;

        _abigailGameType ??= typeof(Game1).Assembly.GetType("StardewValley.Minigames.AbigailGame");
        return minigame.GetType() == _abigailGameType
            || minigame.GetType().FullName == "StardewValley.Minigames.AbigailGame";
    }

    private static void EnsurePrairieKingReflection(object game)
    {
        var type = game.GetType();
        if (_abigailGameReflectedType == type)
            return;

        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags statik = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        _abigailGameType = type;
        _abigailGameReflectedType = type;
        _pkObjectFieldCache.Clear();

        _pkPlayerPositionField = FirstField(type, instance, "playerPosition", "playerPos");
        _pkPlayerBoundingBoxField = FirstField(type, instance, "playerBoundingBox", "playerBox", "playerBounds");
        _pkPlayerMovementDirectionsField = FirstField(type, instance, "playerMovementDirections");
        _pkPlayerShootingDirectionsField = FirstField(type, instance, "playerShootingDirections");
        _pkMonstersField = FirstField(type, statik, "monsters")
            ?? FindCollectionField(type, "CowboyMonster", true);
        _pkBulletsField = FirstField(type, instance, "enemyBullets", "monsterBullets", "bullets")
            ?? FindCollectionField(type, "CowboyBullet", false);
        _pkPowerupsField = FirstField(type, instance, "powerups")
            ?? FindCollectionField(type, "CowboyPowerup", false);
        _pkShoppingField = FirstField(type, instance, "shopping", "isShopping");
        _pkDeathTimerField = FirstField(type, instance, "deathTimer", "playerDeathTimer", "playerDieTimer", "diedTimer");
        _pkBetweenWaveTimerField = FirstField(type, instance, "betweenWaveTimer", "newWaveTimer", "waveStartTimer");
        _pkGameOverField = FirstField(type, instance, "gameOver", "gameOverScreen");
        _pkLivesField = FirstField(type, instance, "lives", "playerLives");
        _pkCoinsField = FirstField(type, instance, "coins", "money");
        _pkWhichWaveField = FirstField(type, instance, "whichWave", "currentWave", "wave");
    }

    private static FieldInfo? FirstField(Type type, BindingFlags flags, params string[] names)
    {
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field;
        }
        return null;
    }

    private static FieldInfo? FindCollectionField(Type type, string elementTypeName, bool includeStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        if (includeStatic)
            flags |= BindingFlags.Static;

        return type.GetFields(flags)
            .FirstOrDefault(field => FieldMentionsType(field.FieldType, elementTypeName));
    }

    private static bool FieldMentionsType(Type type, string typeName)
    {
        if ((type.FullName ?? type.Name).Contains(typeName, StringComparison.Ordinal))
            return true;

        return type.IsGenericType && type.GetGenericArguments()
            .Any(arg => (arg.FullName ?? arg.Name).Contains(typeName, StringComparison.Ordinal));
    }

    private static bool IsPrairieKingShopping(object game)
    {
        return ReadBoolField(game, _pkShoppingField);
    }

    private static bool IsPrairieKingDeathState(object game)
    {
        return ReadBoolField(game, _pkGameOverField) || ReadIntField(game, _pkDeathTimerField, 0) > 0;
    }

    private static bool IsPrairieKingBetweenWaves(object game, int monsterCount)
    {
        return monsterCount == 0 || ReadIntField(game, _pkBetweenWaveTimerField, 0) > 0;
    }

    private static Microsoft.Xna.Framework.Rectangle ReadPrairieKingPlayerBounds(object game)
    {
        var target = _pkPlayerBoundingBoxField?.GetValue(game);
        if (TryReadRectangle(target, out var rect))
            return rect;

        target = _pkPlayerPositionField?.GetValue(game);
        if (TryReadPoint(target, out var point))
            return new Microsoft.Xna.Framework.Rectangle(point.X - 16, point.Y - 16, 32, 32);

        return Microsoft.Xna.Framework.Rectangle.Empty;
    }

    private static List<PrairieKingMonster> ReadPrairieKingMonsters(object game)
    {
        return ReadPrairieKingObjects(game, _pkMonstersField)
            .Select(obj => new PrairieKingMonster(
                ReadObjectRectangle(obj, "position", 32),
                ReadObjectInt(obj, "health", 1),
                ReadObjectInt(obj, "type", -1),
                ReadObjectInt(obj, "speed", 0)))
            .Where(m => m.Bounds.Width > 0 && m.Bounds.Height > 0 && m.Health != 0)
            .ToList();
    }

    private static List<PrairieKingBullet> ReadPrairieKingBullets(object game)
    {
        return ReadPrairieKingObjects(game, _pkBulletsField)
            .Select(obj => new PrairieKingBullet(
                ReadObjectRectangle(obj, "position", 12),
                ReadObjectPoint(obj, "motion"),
                ReadObjectInt(obj, "damage", 1)))
            .Where(b => b.Bounds.Width > 0 && b.Bounds.Height > 0)
            .ToList();
    }

    private static List<PrairieKingPowerup> ReadPrairieKingPowerups(object game)
    {
        return ReadPrairieKingObjects(game, _pkPowerupsField)
            .Select(obj => new PrairieKingPowerup(
                ReadObjectRectangle(obj, "position", 24),
                ReadObjectInt(obj, "which", -1)))
            .Where(p => p.Bounds.Width > 0 && p.Bounds.Height > 0)
            .ToList();
    }

    private static List<object> ReadPrairieKingObjects(object game, FieldInfo? field)
    {
        if (field == null)
            return new List<object>();

        var value = field.GetValue(field.IsStatic ? null : game);
        if (value is not System.Collections.IEnumerable enumerable)
            return new List<object>();

        var result = new List<object>();
        foreach (var entry in enumerable)
        {
            if (entry != null)
                result.Add(entry);
        }
        return result;
    }

    private static Microsoft.Xna.Framework.Rectangle ReadObjectRectangle(object obj, string memberName, int fallbackSize)
    {
        var value = ReadObjectMember(obj, memberName);
        if (TryReadRectangle(value, out var rect))
            return rect;
        if (TryReadPoint(value, out var point))
            return new Microsoft.Xna.Framework.Rectangle(point.X - fallbackSize / 2, point.Y - fallbackSize / 2, fallbackSize, fallbackSize);
        return Microsoft.Xna.Framework.Rectangle.Empty;
    }

    private static Microsoft.Xna.Framework.Point ReadObjectPoint(object obj, string memberName)
    {
        var value = ReadObjectMember(obj, memberName);
        return TryReadPoint(value, out var point) ? point : Microsoft.Xna.Framework.Point.Zero;
    }

    private static int ReadObjectInt(object obj, string memberName, int fallback)
    {
        var value = ReadObjectMember(obj, memberName);
        try { return value == null ? fallback : Convert.ToInt32(value); }
        catch { return fallback; }
    }

    private static object? ReadObjectMember(object obj, string memberName)
    {
        var type = obj.GetType();
        var key = $"{type.FullName}.{memberName}";
        if (!_pkObjectFieldCache.TryGetValue(key, out var field))
        {
            field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _pkObjectFieldCache[key] = field;
        }

        if (field != null)
            return field.GetValue(obj);

        return type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj);
    }

    private static bool TryReadRectangle(object? value, out Microsoft.Xna.Framework.Rectangle rect)
    {
        if (value is Microsoft.Xna.Framework.Rectangle xnaRect)
        {
            rect = xnaRect;
            return true;
        }

        rect = Microsoft.Xna.Framework.Rectangle.Empty;
        return false;
    }

    private static bool TryReadPoint(object? value, out Microsoft.Xna.Framework.Point point)
    {
        switch (value)
        {
            case Microsoft.Xna.Framework.Point xnaPoint:
                point = xnaPoint;
                return true;
            case Vector2 vector:
                point = new Microsoft.Xna.Framework.Point((int)vector.X, (int)vector.Y);
                return true;
            default:
                point = Microsoft.Xna.Framework.Point.Zero;
                return false;
        }
    }

    private static int ReadIntField(object owner, FieldInfo? field, int fallback)
    {
        if (field == null)
            return fallback;

        try
        {
            var value = field.GetValue(field.IsStatic ? null : owner);
            return value == null ? fallback : Convert.ToInt32(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBoolField(object owner, FieldInfo? field)
    {
        if (field == null)
            return false;

        try
        {
            var value = field.GetValue(field.IsStatic ? null : owner);
            return value is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    private sealed record PrairieKingMonster(
        Microsoft.Xna.Framework.Rectangle Bounds,
        int Health,
        int Type,
        int Speed);

    private sealed record PrairieKingBullet(
        Microsoft.Xna.Framework.Rectangle Bounds,
        Microsoft.Xna.Framework.Point Motion,
        int Damage);

    private sealed record PrairieKingPowerup(
        Microsoft.Xna.Framework.Rectangle Bounds,
        int Which);

    private object HandleMenu()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var menu = Game1.activeClickableMenu;
                if (menu == null)
                {
                    object? eventInfo = null;
                    if (Game1.currentLocation?.currentEvent != null)
                    {
                        var ev = Game1.currentLocation.currentEvent;
                        eventInfo = new { id = ev.id, skippable = ev.skippable };
                    }
                    tcs.SetResult(new { ok = true, open = false, activeEvent = eventInfo });
                    return;
                }

                var menuType = menu.GetType().Name;
                string? dialogue = null;
                List<object>? responses = null;
                List<object>? shopItems = null;
                List<object>? buttons = null;

                if (menu is DialogueBox db)
                {
                    try { dialogue = db.getCurrentString(); } catch { }

                    var responseField = typeof(DialogueBox).GetField("responseCC",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseCCs = responseField?.GetValue(db) as List<ClickableComponent>;

                    var responsesField = typeof(DialogueBox).GetField("responses",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseList = responsesField?.GetValue(db) as List<Response>;

                    if (responseList != null && responseList.Count > 0)
                    {
                        responses = new List<object>();
                        for (int i = 0; i < responseList.Count; i++)
                        {
                            var r = responseList[i];
                            responses.Add(new
                            {
                                index = i,
                                key = r.responseKey,
                                text = r.responseText,
                                bounds = responseCCs != null && i < responseCCs.Count
                                    ? new { x = responseCCs[i].bounds.X, y = responseCCs[i].bounds.Y,
                                            w = responseCCs[i].bounds.Width, h = responseCCs[i].bounds.Height }
                                    : null
                            });
                        }
                    }
                }
                else if (menu is ShopMenu shop)
                {
                    shopItems = new List<object>();
                    var forSale = shop.forSale;
                    var itemPriceAndStock = shop.itemPriceAndStock;
                    foreach (var item in forSale)
                    {
                        int price = 0;
                        int stock = -1;
                        if (itemPriceAndStock.TryGetValue(item, out var info))
                        {
                            price = info.Price;
                            stock = info.Stock;
                        }
                        shopItems.Add(new
                        {
                            name = item.DisplayName,
                            id = item.QualifiedItemId,
                            price,
                            stock
                        });
                    }
                }

                // Collect named buttons via reflection
                buttons = new List<object>();
                foreach (var fieldName in new[] { "okButton", "cancelButton", "backButton",
                    "forwardButton", "upperRightCloseButton", "trashCan" })
                {
                    var field = menu.GetType().GetField(fieldName,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var comp = field?.GetValue(menu) as ClickableComponent;
                    if (comp != null && comp.visible)
                    {
                        buttons.Add(new
                        {
                            name = fieldName,
                            x = comp.bounds.Center.X,
                            y = comp.bounds.Center.Y
                        });
                    }
                }

                tcs.SetResult(new
                {
                    ok = true,
                    open = true,
                    type = menuType,
                    dialogue,
                    responses,
                    shopItems,
                    buttons = buttons.Count > 0 ? buttons : null
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMenuClick(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var option = GetParamOr(p, "option", -1);
        var button = GetParamOr(p, "button", "");
        var clickX = GetParamOr(p, "x", -1);
        var clickY = GetParamOr(p, "y", -1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var menu = Game1.activeClickableMenu;
                if (menu == null)
                {
                    tcs.SetResult(new { ok = false, error = "No menu open" });
                    return;
                }

                if (option >= 0 && menu is DialogueBox db)
                {
                    var responseField = typeof(DialogueBox).GetField("responseCC",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseCCs = responseField?.GetValue(db) as List<ClickableComponent>;

                    if (responseCCs != null && option < responseCCs.Count)
                    {
                        var rc = responseCCs[option];
                        db.receiveLeftClick(rc.bounds.Center.X, rc.bounds.Center.Y);
                        tcs.SetResult(new { ok = true, clicked = "response", option });
                    }
                    else
                    {
                        tcs.SetResult(new { ok = false, error = $"Response index {option} out of range" });
                    }
                    return;
                }

                if (button != "")
                {
                    var field = menu.GetType().GetField(button == "ok" ? "okButton" :
                                                        button == "cancel" ? "cancelButton" :
                                                        button == "back" ? "backButton" :
                                                        button == "forward" ? "forwardButton" :
                                                        button == "close" ? "upperRightCloseButton" :
                                                        button,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var comp = field?.GetValue(menu) as ClickableComponent;
                    if (comp != null)
                    {
                        menu.receiveLeftClick(comp.bounds.Center.X, comp.bounds.Center.Y);
                        tcs.SetResult(new { ok = true, clicked = "button", button });
                    }
                    else
                    {
                        tcs.SetResult(new { ok = false, error = $"Button '{button}' not found" });
                    }
                    return;
                }

                if (clickX >= 0 && clickY >= 0)
                {
                    menu.receiveLeftClick(clickX, clickY);
                    tcs.SetResult(new { ok = true, clicked = "position", x = clickX, y = clickY });
                    return;
                }

                menu.receiveLeftClick(
                    menu.xPositionOnScreen + menu.width / 2,
                    menu.yPositionOnScreen + menu.height / 2);
                tcs.SetResult(new { ok = true, clicked = "center" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleCraft(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var name = GetParam<string>(p, "name");
        var count = GetParamOr(p, "count", 1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;
                var recipes = CraftingRecipe.craftingRecipes;
                if (!recipes.ContainsKey(name))
                {
                    var known = farmer.craftingRecipes.Keys.ToList();
                    tcs.SetResult(new { ok = false, error = $"Recipe '{name}' not found",
                        knownRecipes = known });
                    return;
                }

                if (!farmer.craftingRecipes.ContainsKey(name))
                {
                    tcs.SetResult(new { ok = false, error = $"Player hasn't learned recipe '{name}'" });
                    return;
                }

                var recipe = new CraftingRecipe(name, false);
                int crafted = 0;
                var missing = new Dictionary<string, int>();

                for (int i = 0; i < count; i++)
                {
                    if (!recipe.doesFarmerHaveIngredientsInInventory())
                    {
                        foreach (var kvp in recipe.recipeList)
                        {
                            var ingredientId = kvp.Key;
                            var needed = kvp.Value;
                            var have = 0;
                            foreach (var item in farmer.Items)
                            {
                                if (item != null && (item.ParentSheetIndex.ToString() == ingredientId
                                    || item.Category.ToString() == ingredientId))
                                    have += item.Stack;
                            }
                            if (have < needed)
                            {
                                var ingredientName = ingredientId;
                                try { ingredientName = new StardewValley.Object(ingredientId, 1).DisplayName; } catch { }
                                missing[ingredientName] = needed - have;
                            }
                        }
                        break;
                    }
                    recipe.consumeIngredients(null);
                    var product = recipe.createItem();
                    if (!farmer.addItemToInventoryBool(product))
                    {
                        Game1.createItemDebris(product, farmer.getStandingPosition(), farmer.FacingDirection);
                        tcs.SetResult(new { ok = true, crafted = crafted + 1,
                            warning = "Inventory full, item dropped" });
                        return;
                    }
                    crafted++;
                }

                if (crafted == 0)
                    tcs.SetResult(new { ok = false, error = "Missing materials", missing });
                else if (crafted < count)
                    tcs.SetResult(new { ok = true, crafted, requested = count,
                        warning = "Ran out of materials", missing });
                else
                    tcs.SetResult(new { ok = true, crafted });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMachines()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.player.currentLocation;
                var machines = new List<object>();

                foreach (var pair in loc.objects.Pairs)
                {
                    var obj = pair.Value;
                    if (!obj.bigCraftable.Value) continue;

                    string status;
                    if (obj.readyForHarvest.Value)
                        status = "ready";
                    else if (obj.heldObject.Value != null || obj.MinutesUntilReady > 0)
                        status = "processing";
                    else
                        status = "empty";

                    var entry = new Dictionary<string, object?>
                    {
                        ["name"] = obj.Name,
                        ["x"] = (int)pair.Key.X,
                        ["y"] = (int)pair.Key.Y,
                        ["status"] = status,
                        ["minutesLeft"] = obj.MinutesUntilReady
                    };

                    if (obj.heldObject.Value != null)
                    {
                        entry["heldItem"] = obj.heldObject.Value.Name;
                        entry["heldItemId"] = obj.heldObject.Value.QualifiedItemId;
                    }

                    machines.Add(entry);
                }

                tcs.SetResult(new
                {
                    ok = true,
                    location = loc.Name,
                    count = machines.Count,
                    machines
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleAnimals()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.player.currentLocation;
                var animals = new List<object>();

                IEnumerable<FarmAnimal>? animalList = null;
                if (loc is Farm farm)
                    animalList = farm.animals.Values;
                else if (loc is AnimalHouse ah)
                    animalList = ah.animals.Values;

                if (animalList != null)
                {
                    foreach (var a in animalList)
                    {
                        animals.Add(new
                        {
                            name = a.Name,
                            type = a.type.Value,
                            x = a.TilePoint.X,
                            y = a.TilePoint.Y,
                            wasPetToday = a.wasPet.Value,
                            friendship = a.friendshipTowardFarmer.Value,
                            happiness = a.happiness.Value,
                            fullness = a.fullness.Value,
                            age = a.age.Value,
                            home = a.home?.indoors.Value?.Name,
                            product = a.currentProduce.Value,
                            productReady = a.currentProduce.Value != null && a.currentProduce.Value != "-1"
                        });
                    }
                }

                tcs.SetResult(new
                {
                    ok = true,
                    location = loc.Name,
                    count = animals.Count,
                    animals
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private Vector2 GetFacingTile(Farmer farmer)
    {
        int x = farmer.TilePoint.X;
        int y = farmer.TilePoint.Y;
        return farmer.FacingDirection switch
        {
            0 => new Vector2(x, y - 1),
            1 => new Vector2(x + 1, y),
            2 => new Vector2(x, y + 1),
            3 => new Vector2(x - 1, y),
            _ => new Vector2(x, y)
        };
    }

    // --- Helpers ---

    private void EnqueueMainThread(Action action)
    {
        lock (_queueLock)
        {
            _mainThreadQueue.Enqueue(action);
        }
    }

    /// <summary>
    /// Simple BFS pathfinding on the game map.
    /// </summary>
    private Queue<Point>? FindPath(GameLocation location, Point start, Point end)
    {
        if (start == end) return new Queue<Point>();

        var maxSteps = 500;
        var visited = new HashSet<Point> { start };
        var queue = new Queue<(Point pos, List<Point> path)>();
        queue.Enqueue((start, new List<Point>()));

        int[] dx = { 0, 0, -1, 1 };
        int[] dy = { -1, 1, 0, 0 };

        while (queue.Count > 0 && maxSteps-- > 0)
        {
            var (pos, path) = queue.Dequeue();

            for (int i = 0; i < 4; i++)
            {
                var next = new Point(pos.X + dx[i], pos.Y + dy[i]);

                if (visited.Contains(next)) continue;
                if (!IsTilePassable(location, next)) continue;

                visited.Add(next);
                var newPath = new List<Point>(path) { next };

                if (next == end)
                    return new Queue<Point>(newPath);

                queue.Enqueue((next, newPath));
            }
        }

        // If no path found, return null (caller will fallback to direct walk)
        return null;
    }

    private bool IsTilePassable(GameLocation location, Point tile)
    {
        // Check map bounds
        if (tile.X < 0 || tile.Y < 0) return false;
        var mapWidth = location.Map.DisplayWidth / 64;
        var mapHeight = location.Map.DisplayHeight / 64;
        if (tile.X >= mapWidth || tile.Y >= mapHeight) return false;

        // Use the game's built-in passability check
        var tileVec = new Vector2(tile.X, tile.Y);
        return location.isTilePassable(tileVec);
    }
}
