using System;
using System.Collections.Generic;
using cantorsdust.Common;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using TimeSpeed.Framework;
using TimeSpeed.Framework.Messages;

namespace TimeSpeed;

/// <summary>The entry class called by SMAPI.</summary>
internal class ModEntry : Mod
{
    /*********
    ** Properties
    *********/
    /// <summary>Provides helper methods for tracking time flow.</summary>
    private readonly TimeHelper TimeHelper = new();

    /// <summary>Displays messages to the user.</summary>
    private Notifier Notifier = null!; // set in Entry

    /// <summary>The mod configuration.</summary>
    private ModConfig Config = null!; // set in Entry

    /// <summary>Whether the player has manually frozen time.</summary>
    private bool ManualFreeze;

    /// <summary>The reason time would be frozen automatically if applicable, regardless of <see cref="ManualFreeze"/>.</summary>
    private AutoFreezeReason AutoFreeze = AutoFreezeReason.None;

    /// <summary>The current auto-freeze reasons which the player has temporarily suspended until the relevant context changes.</summary>
    private readonly HashSet<AutoFreezeReason> SuspendAutoFreezes = [];

    /// <summary>Whether time should be frozen.</summary>
    private bool IsTimeFrozen =>
        !this.InCutsceneUnfreeze
        && (this.ManualFreeze
            || (this.AutoFreeze != AutoFreezeReason.None && !this.SuspendAutoFreezes.Contains(this.AutoFreeze)));

    /// <summary>Whether the flow of time should be adjusted.</summary>
    private bool AdjustTime;

    /// <summary>Whether a cutscene/event was active on the previous tick.</summary>
    private bool WasInCutscene;

    /// <summary>Whether time was frozen before the current cutscene started.</summary>
    private bool WasTimeFrozenBeforeCutscene;

    /// <summary>The tick count when the cutscene ended. Used for delay buffer.</summary>
    private uint CutsceneEndedAtTick;

    /// <summary>Whether we're in the post-cutscene delay period.</summary>
    private bool InPostCutsceneDelay;

    /// <summary>Number of ticks to wait after a cutscene ends before re-freezing time (~1 second).</summary>
    private const uint PostCutsceneDelayTicks = 60;

    /// <summary>Whether time is temporarily unfrozen for a cutscene.</summary>
    private bool InCutsceneUnfreeze;

    /// <summary>Whether a real event/cutscene (not just a screen fade) occurred during the current cutscene unfreeze period.</summary>
    private bool CutsceneHadEvent;

    /// <summary>The saved gameTimeInterval value from before the game update. Used to restore it after the update.</summary>
    private int SavedGameTimeInterval;

    /// <summary>The saved Game1.timeOfDay when the cutscene chain started. Restored after the cutscene ends to prevent clock leakage.</summary>
    private int SavedTimeOfDayBeforeCutscene;

    /// <summary>The saved Game1.gameTimeInterval when the cutscene chain started. Restored after the cutscene ends to prevent clock leakage.</summary>
    private int SavedGameTimeIntervalBeforeCutscene;

    /// <summary>Whether time should be frozen this tick. Set in UpdateTicking, used in UpdateTicked.</summary>
    private bool FreezeTimeThisTick;

    /// <summary>Backing field for <see cref="TickInterval"/>.</summary>
    private int _tickInterval;

    /// <summary>The number of milliseconds per 10-game-minutes to apply.</summary>
    private int TickInterval
    {
        get => this._tickInterval;
        set => this._tickInterval = Math.Max(value, 0);
    }


    /*********
    ** Public methods
    *********/
    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        // init
        I18n.Init(helper.Translation);
        CommonHelper.RemoveObsoleteFiles(this, "TimeSpeed.pdb");
        this.Notifier = new Notifier(this.Helper.Multiplayer, this.ModManifest.UniqueID, this.Monitor);

        // read config
        this.Config = helper.ReadConfig<ModConfig>();

        // add time events
        this.TimeHelper.WhenTickProgressChanged(this.OnTickProgressed);
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.UpdateTicking += this.OnUpdateTicking;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;
        helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
        helper.Events.Player.Warped += this.OnWarped;

        // add time freeze/unfreeze notification
        {
            bool wasPaused = false;
            helper.Events.Display.RenderingHud += (_, _) =>
            {
                wasPaused = Game1.paused;
                if (this.IsTimeFrozen)
                    Game1.paused = true;
            };

            helper.Events.Display.RenderedHud += (_, _) =>
            {
                Game1.paused = wasPaused;
            };
        }
    }


    /*********
    ** Private methods
    *********/
    /****
    ** Event handlers
    ****/
    /// <inheritdoc cref="IGameLoopEvents.GameLaunched"/>
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.RegisterConfigMenu();
    }

    /// <inheritdoc cref="IGameLoopEvents.SaveLoaded"/>
    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.RegisterConfigMenu();

        // reset cutscene tracking state
        this.WasInCutscene = false;
        this.WasTimeFrozenBeforeCutscene = false;
        this.CutsceneEndedAtTick = 0;
        this.InPostCutsceneDelay = false;
        this.InCutsceneUnfreeze = false;
        this.CutsceneHadEvent = false;
        this.SavedTimeOfDayBeforeCutscene = 0;
        this.SavedGameTimeIntervalBeforeCutscene = 0;
    }

    /// <inheritdoc cref="IMultiplayerEvents.ModMessageReceived"/>
    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != this.ModManifest.UniqueID || e.FromPlayerID == Game1.player.UniqueMultiplayerID)
            return;

        switch (e.Type)
        {
            // from farmhand: request to (un)freeze time
            case nameof(ToggleFreezeMessage):
                if (Context.IsMainPlayer)
                {
                    if (!this.Config.LetFarmhandsManageTime)
                        this.RejectRequestFromFarmhand("toggle time freeze", e.FromPlayerID);
                    else
                        this.ToggleFreeze(fromPlayerId: e.FromPlayerID);
                }

                break;

            // from farmhand: request to change time speed
            case nameof(ChangeTickIntervalMessage):
                if (Context.IsMainPlayer)
                {
                    if (!this.Config.LetFarmhandsManageTime)
                        this.RejectRequestFromFarmhand("change time speed", e.FromPlayerID);
                    else
                    {
                        var message = e.ReadAs<ChangeTickIntervalMessage>();
                        this.ChangeTickInterval(message.Increase, message.Change, fromPlayerId: e.FromPlayerID);
                    }
                }
                break;

            // from host: access denied
            case nameof(RequestDeniedMessage):
                this.Notifier.OnAccessDeniedFromHost(I18n.Message_HostAccessDenied());
                break;

            // from host: time speed changed
            case nameof(NotifyTickIntervalChangedMessage):
                if (!Context.IsMainPlayer)
                {
                    var message = e.ReadAs<NotifyTickIntervalChangedMessage>();
                    this.Notifier.OnSpeedChanged(message.NewInterval, fromPlayerId: message.FromPlayerId);
                }
                break;

            // from host: time (un)frozen
            case nameof(NotifyFreezeChangedMessage):
                if (!Context.IsMainPlayer)
                {
                    var message = e.ReadAs<NotifyFreezeChangedMessage>();
                    this.Notifier.OnTimeFreezeToggled(frozen: message.IsFrozen, fromPlayerId: message.FromPlayerId);
                }
                break;
        }
    }

    /// <inheritdoc cref="IGameLoopEvents.DayStarted"/>
    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!this.ShouldEnable())
            return;

        this.UpdateScaleForDay(Game1.season, Game1.dayOfMonth);
        this.UpdateTimeFreeze(clearPreviousOverrides: true);
        this.UpdateSettingsForLocation(Game1.currentLocation);
    }

    /// <inheritdoc cref="IInputEvents.ButtonsChanged"/>
    private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        if (!this.ShouldEnable(forInput: true))
            return;

        if (this.Config.Keys.FreezeTime.JustPressed())
            this.ToggleFreeze();
        else if (this.Config.Keys.IncreaseTickInterval.JustPressed())
            this.ChangeTickInterval(increase: true);
        else if (this.Config.Keys.DecreaseTickInterval.JustPressed())
            this.ChangeTickInterval(increase: false);
        else if (this.Config.Keys.ReloadConfig.JustPressed())
            this.ReloadConfig();
    }

    /// <inheritdoc cref="IPlayerEvents.Warped"/>
    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!this.ShouldEnable() || !e.IsLocalPlayer)
            return;

        this.UpdateSettingsForLocation(e.NewLocation);
    }

    /// <inheritdoc cref="IGameLoopEvents.TimeChanged"/>
    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (!this.ShouldEnable())
            return;

        this.UpdateFreezeForTime();
    }

    /// <summary>Raised before the game state is updated (~60 times per second).</summary>
    /// <remarks>
    /// Saves the current gameTimeInterval BEFORE the game increments it.
    /// This allows us to restore it after the update, effectively preventing
    /// time from advancing while keeping gameTimeInterval at a natural non-zero
    /// value. This avoids compatibility issues with mods (like Chatter) that
    /// depend on game systems behaving normally when gameTimeInterval is
    /// not forcibly zeroed.
    /// </remarks>
    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        this.FreezeTimeThisTick = false;

        // don't freeze during cutscenes or post-cutscene delay
        if (this.Config.UnpauseTimeDuringCutscenes)
        {
            if (this.IsCutscenePlaying())
                return;
            if (this.InPostCutsceneDelay)
                return;
        }

        if (Game1.showingEndOfNightStuff)
            return;

        if (this.IsTimeFrozen)
        {
            this.FreezeTimeThisTick = true;
            // Cap at a safe value well below the 7000ms threshold that triggers
            // a 10-minute clock advance, so the game update can't cross it
            this.SavedGameTimeInterval = Math.Min(Game1.gameTimeInterval, 100);
        }
    }

    /// <inheritdoc cref="IGameLoopEvents.UpdateTicked"/>
    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!this.ShouldEnable())
            return;

        this.TimeHelper.Update();

        // handle cutscene transitions
        if (this.Config.UnpauseTimeDuringCutscenes)
        {
            bool isInCutscene = this.IsCutscenePlaying();

            // cutscene just started
            if (isInCutscene && !this.WasInCutscene)
            {
                // Only evaluate and save when not already tracking a cutscene chain.
                // If InCutsceneUnfreeze is already true, a prior cutscene's snapshot
                // is still active — keep it so the clock restores to the original
                // pre-chain value even across consecutive cutscenes.
                if (!this.InCutsceneUnfreeze)
                {
                    this.WasTimeFrozenBeforeCutscene = this.IsTimeFrozen;
                    if (this.WasTimeFrozenBeforeCutscene)
                    {
                        this.SavedTimeOfDayBeforeCutscene = Game1.timeOfDay;
                        this.SavedGameTimeIntervalBeforeCutscene = Game1.gameTimeInterval;
                    }
                }
                this.InPostCutsceneDelay = false;
                this.InCutsceneUnfreeze = true;
                this.CutsceneHadEvent = false;
                this.Monitor.Log($"Cutscene started. Time was {(this.WasTimeFrozenBeforeCutscene ? "frozen" : "not frozen")} before.", LogLevel.Trace);
            }
            // cutscene just ended
            else if (!isInCutscene && this.WasInCutscene)
            {
                if (this.CutsceneHadEvent)
                {
                    // real event ended — apply delay so the post-event fade-in can finish
                    this.CutsceneEndedAtTick = e.Ticks;
                    this.InPostCutsceneDelay = true;
                    this.Monitor.Log($"Cutscene ended. Starting {PostCutsceneDelayTicks} tick delay before restoring freeze state.", LogLevel.Trace);
                }
                else
                {
                    // was just a screen fade (warp/transition), not a real event — restore immediately
                    this.RestoreTimeAfterCutscene();
                    this.InPostCutsceneDelay = false;
                    this.InCutsceneUnfreeze = false;
                    this.Monitor.Log("Screen fade ended (no event detected). Restoring freeze state immediately.", LogLevel.Trace);
                }
            }

            // track whether a real event occurred during this cutscene period
            if (isInCutscene && this.InCutsceneUnfreeze && !this.CutsceneHadEvent)
            {
                if (Game1.eventUp || Game1.CurrentEvent != null || Game1.currentMinigame != null)
                    this.CutsceneHadEvent = true;
            }

            this.WasInCutscene = isInCutscene;

            // check if post-cutscene delay has elapsed
            if (this.InPostCutsceneDelay && e.Ticks - this.CutsceneEndedAtTick >= PostCutsceneDelayTicks)
            {
                this.RestoreTimeAfterCutscene();
                this.InPostCutsceneDelay = false;
                this.InCutsceneUnfreeze = false;
                this.Monitor.Log($"Post-cutscene delay complete. Restoring time freeze state: {(this.WasTimeFrozenBeforeCutscene ? "frozen" : "not frozen")}.", LogLevel.Trace);
            }
        }

        // restore the saved gameTimeInterval to prevent time from advancing
        if (this.FreezeTimeThisTick)
        {
            Game1.gameTimeInterval = this.SavedGameTimeInterval;
        }

        if (e.IsOneSecond && this.Monitor.IsVerbose)
        {
            string? timeFrozenLabel;
            if (this.ManualFreeze)
                timeFrozenLabel = ", frozen manually";
            else if (this.SuspendAutoFreezes.Contains(this.AutoFreeze))
                timeFrozenLabel = ", resumed manually";
            else if (this.IsTimeFrozen)
                timeFrozenLabel = $", frozen per {this.AutoFreeze}";
            else
                timeFrozenLabel = null;

            this.Monitor.Log($"Time is {Game1.timeOfDay}; {this.TimeHelper.TickProgress:P} towards {Utility.ModifyTime(Game1.timeOfDay, 10)} (tick interval: {this.TimeHelper.CurrentDefaultTickInterval}, {this.TickInterval / 10_000m:0.##}s/min{timeFrozenLabel})");
        }
    }

    /// <summary>Raised after the <see cref="TimeHelper.TickProgress"/> value changes.</summary>
    /// <remarks>
    /// When time is frozen, the save/restore mechanism in OnUpdateTicking/OnUpdateTicked
    /// handles keeping gameTimeInterval stable. This handler only needs to handle
    /// time scaling when not frozen.
    /// </remarks>
    private void OnTickProgressed(object? sender, TickProgressChangedEventArgs e)
    {
        if (!this.ShouldEnable())
            return;

        // save/restore in UpdateTicking/UpdateTicked handles freezing
        if (this.IsTimeFrozen)
            return;

        if (!this.AdjustTime)
            return;
        if (this.TickInterval == 0)
            this.TickInterval = 1000;

        if (e.TimeChanged)
            this.TimeHelper.TickProgress = this.ScaleTickProgress(this.TimeHelper.TickProgress, this.TickInterval);
        else
            this.TimeHelper.TickProgress = e.PreviousProgress + this.ScaleTickProgress(e.NewProgress - e.PreviousProgress, this.TickInterval);
    }

    /****
    ** Methods
    ****/
    /// <summary>Get whether time features should be enabled.</summary>
    /// <param name="forInput">Whether to check for input handling.</param>
    private bool ShouldEnable(bool forInput = false)
    {
        // save is loaded
        if (!Context.IsWorldReady)
            return false;

        // must be host player to directly control time
        if (!Context.IsMainPlayer && !forInput)
            return false;

        // check restrictions for input
        if (forInput)
        {
            // don't handle input when player isn't free (except in events)
            if (!Context.IsPlayerFree && !Game1.eventUp)
                return false;

            // ignore input if a textbox is active
            if (Game1.keyboardDispatcher.Subscriber is not null)
                return false;
        }

        return true;
    }

    /// <summary>Reload <see cref="Config"/> from the config file.</summary>
    private void ReloadConfig()
    {
        this.Config = this.Helper.ReadConfig<ModConfig>();
        this.UpdateScaleForDay(Game1.season, Game1.dayOfMonth);
        this.UpdateSettingsForLocation(Game1.currentLocation);
        this.Notifier.OnConfigReloaded();
    }

    /// <summary>Register or update the config menu with Generic Mod Config Menu.</summary>
    private void RegisterConfigMenu()
    {
        GenericModConfigMenuIntegration.Register(this.ModManifest, this.Helper.ModRegistry, this.Monitor,
            getConfig: () => this.Config,
            reset: () => this.Config = new ModConfig(),
            save: () =>
            {
                this.Helper.WriteConfig(this.Config);
                if (this.ShouldEnable())
                    this.UpdateSettingsForLocation(Game1.currentLocation);
            }
        );
    }

    /// <summary>Increment or decrement the tick interval, taking into account the held modifier key if applicable.</summary>
    /// <param name="increase">Whether to increment the tick interval; else decrement.</param>
    /// <param name="amount">The absolute amount by which to change the tick interval, or <c>null</c> to get the default amount based on the local pressed keys.</param>
    /// <param name="fromPlayerId">The player which requested the change, if applicable.</param>
    private void ChangeTickInterval(bool increase, int? amount = null, long? fromPlayerId = null)
    {
        // get offset to apply
        int change = amount ?? 1000;
        if (!amount.HasValue)
        {
            KeyboardState state = Keyboard.GetState();
            if (state.IsKeyDown(Keys.LeftControl))
                change *= 100;
            else if (state.IsKeyDown(Keys.LeftShift))
                change *= 10;
            else if (state.IsKeyDown(Keys.LeftAlt))
                change /= 10;
        }

        // ask host to change the tick interval if needed
        if (!Context.IsMainPlayer)
        {
            this.SendMessageToHost(new ChangeTickIntervalMessage { Change = change, Increase = increase });
            return;
        }

        // update tick interval
        if (!increase)
        {
            int minAllowed = Math.Min(this.TickInterval, change);
            this.TickInterval = Math.Max(minAllowed, this.TickInterval - change);
        }
        else
            this.TickInterval += change;

        // log change
        this.Notifier.OnSpeedChanged(this.TickInterval, fromPlayerId);
    }

    /// <summary>Toggle whether time is frozen.</summary>
    /// <param name="fromPlayerId">The player which requested the change, if applicable.</param>
    private void ToggleFreeze(long? fromPlayerId = null)
    {
        // ask host to toggle freeze if needed
        if (!Context.IsMainPlayer)
        {
            this.SendMessageToHost(new ToggleFreezeMessage());
            return;
        }

        // apply
        bool freeze = !this.IsTimeFrozen;
        this.UpdateTimeFreeze(manualOverride: freeze);
        this.Notifier.OnTimeFreezeToggled(frozen: freeze, fromPlayerId: fromPlayerId);
    }

    /// <summary>Update the time freeze settings for the given time of day.</summary>
    private void UpdateFreezeForTime()
    {
        bool wasFrozen = this.IsTimeFrozen;
        this.UpdateTimeFreeze();

        if (!wasFrozen && this.IsTimeFrozen)
            this.Notifier.OnTimeFreezeToggled(frozen: true, logMessage: $"Time automatically set to frozen at {Game1.timeOfDay}.");
    }

    /// <summary>Update the time settings for the given location.</summary>
    /// <param name="location">The game location.</param>
    private void UpdateSettingsForLocation(GameLocation? location)
    {
        if (location == null)
            return;

        // update time settings
        this.SuspendAutoFreezes.Remove(AutoFreezeReason.FrozenForLocation);
        this.UpdateTimeFreeze();
        this.TickInterval = this.Config.GetMillisecondsPerMinute(location) * 10;

        // notify player
        if (this.Config.LocationNotify)
            this.Notifier.OnLocalLocationChanged(this.IsTimeFrozen, this.TickInterval, this.AutoFreeze);
    }

    /// <summary>Update the <see cref="AutoFreeze"/> and <see cref="ManualFreeze"/> flags based on the current context.</summary>
    /// <param name="manualOverride">An explicit freeze (<c>true</c>) or unfreeze (<c>false</c>) requested by the player, if applicable.</param>
    /// <param name="clearPreviousOverrides">Whether to clear any previous explicit overrides.</param>
    private void UpdateTimeFreeze(bool? manualOverride = null, bool clearPreviousOverrides = false)
    {
        bool wasManualFreeze = this.ManualFreeze;
        AutoFreezeReason wasAutoFreeze = this.AutoFreeze;

        // update auto freeze
        this.AutoFreeze = this.GetAutoFreezeType();
        bool isAutoFrozen = this.AutoFreeze != AutoFreezeReason.None;

        // update manual freeze
        if (manualOverride.HasValue)
            this.ManualFreeze = manualOverride.Value;

        // update overrides
        if (clearPreviousOverrides || !isAutoFrozen)
            this.SuspendAutoFreezes.Clear();
        if (manualOverride == false && isAutoFrozen)
            this.SuspendAutoFreezes.Add(this.AutoFreeze);

        // log change
        if (wasAutoFreeze != this.AutoFreeze)
            this.Monitor.Log($"Auto freeze changed from {wasAutoFreeze} to {this.AutoFreeze}.");
        if (wasManualFreeze != this.ManualFreeze)
            this.Monitor.Log($"Manual freeze changed from {wasManualFreeze} to {this.ManualFreeze}.");
    }

    /// <summary>Update the time settings for the given date.</summary>
    /// <param name="season">The current season.</param>
    /// <param name="dayOfMonth">The current day of month.</param>
    private void UpdateScaleForDay(Season season, int dayOfMonth)
    {
        this.AdjustTime = this.Config.ShouldScale(season, dayOfMonth);
    }

    /// <summary>Get the adjusted progress towards the next 10-game-minute tick.</summary>
    /// <param name="progress">The percentage of the clock tick interval (i.e. the interval between time changes) that elapsed since the last update tick.</param>
    /// <param name="newTickInterval">The clock tick interval to which to apply the progress.</param>
    private double ScaleTickProgress(double progress, int newTickInterval)
    {
        double ratio = this.TimeHelper.CurrentDefaultTickInterval / (newTickInterval * 1d); // ratio between the game's normal interval (e.g. 7000) and the player's custom interval
        return progress * ratio;
    }

    /// <summary>Get the freeze type which applies for the current context, ignoring overrides by the player.</summary>
    private AutoFreezeReason GetAutoFreezeType()
    {
        if (this.Config.ShouldFreeze(Game1.currentLocation))
            return AutoFreezeReason.FrozenForLocation;

        if (this.Config.ShouldFreezeBeforePassingOut(Game1.timeOfDay))
            return AutoFreezeReason.FrozenBeforePassOut;

        if (this.Config.ShouldFreeze(Game1.timeOfDay))
            return AutoFreezeReason.FrozenAtTime;

        return AutoFreezeReason.None;
    }

    /// <summary>Check if a cutscene, event, or other non-interactive sequence is currently playing.</summary>
    /// <remarks>
    /// This method detects various game states where time should pass normally, including:
    /// - Standard events/cutscenes (vanilla and modded, e.g., SVE, Ridgeside Village, East Scarp)
    /// - Mini-games (fishing, arcade games, etc.)
    /// - Screen transitions and warps
    /// - Movie theater sequences
    /// - Dialogue sequences that are part of events
    /// Festivals are explicitly excluded — they allow free movement between locations
    /// (e.g., the Desert Festival lets you enter Skull Cavern), so time freeze during
    /// festivals is handled by normal location-based config.
    /// </remarks>
    private bool IsCutscenePlaying()
    {
        // Festivals allow free movement, so they are NOT cutscenes.
        // This must come before eventUp/CurrentEvent because festivals set both.
        if (Game1.isFestival())
            return false;

        // standard event/cutscene detection — catches ALL mod events
        if (Game1.eventUp)
            return true;
        if (Game1.CurrentEvent != null)
            return true;

        // mini-game detection
        if (Game1.currentMinigame != null)
            return true;

        // screen fade detection — catches the fade-to-black phase before eventUp is set,
        // which some cutscenes (especially modded) need time flowing to initialize.
        // The post-cutscene delay is only applied when a real event occurred (tracked by
        // CutsceneHadEvent), so plain warp fades don't leak time.
        if (Game1.globalFade)
            return true;
        if (Game1.fadeToBlack)
            return true;

        // movie theater playback
        if (Game1.currentLocation is MovieTheater)
        {
            if (Game1.activeClickableMenu != null &&
                Game1.activeClickableMenu.GetType().Name.Contains("Movie"))
                return true;
        }

        // event-related menus
        if (Game1.activeClickableMenu != null)
        {
            var menuName = Game1.activeClickableMenu.GetType().Name;

            if (menuName.Contains("Event"))
                return true;
        }

        return false;
    }

    /// <summary>Restore the game clock to its pre-cutscene state if time was frozen before the cutscene started.</summary>
    private void RestoreTimeAfterCutscene()
    {
        if (this.WasTimeFrozenBeforeCutscene)
        {
            Game1.timeOfDay = this.SavedTimeOfDayBeforeCutscene;
            Game1.gameTimeInterval = this.SavedGameTimeIntervalBeforeCutscene;
            this.Monitor.Log($"Restored clock after cutscene: timeOfDay={Game1.timeOfDay}, interval={Game1.gameTimeInterval}.", LogLevel.Trace);
        }
    }

    /// <summary>Send a multiplayer message to the host player.</summary>
    /// <typeparam name="TMessage">The message type to send.</typeparam>
    /// <param name="message">The message to send.</param>
    private void SendMessageToHost<TMessage>(TMessage message)
        where TMessage : notnull
    {
        // check host info
        long hostPlayerId = Game1.MasterPlayer.UniqueMultiplayerID;
        IMultiplayerPeerMod? hostMod = this.Helper.Multiplayer.GetConnectedPlayer(hostPlayerId)?.GetMod(this.ModManifest.UniqueID);
        if (hostMod is null || hostMod.Version.IsOlderThan("2.8.0"))
        {
            this.Notifier.OnAccessDeniedFromHost(I18n.Message_HostMissingMod());
            return;
        }

        // send message
        string messageType = message.GetType().Name;
        this.Helper.Multiplayer.SendMessage(message, messageType, modIDs: [this.ModManifest.UniqueID], playerIDs: [hostPlayerId]);
    }

    /// <summary>Reject a request to control the time from a farmhand.</summary>
    /// <param name="actionLabel">A human-readable label for the attempted change (like 'toggle time freeze') for logged messages.</param>
    /// <param name="farmhandId">The farmhand who requested the change.</param>
    private void RejectRequestFromFarmhand(string actionLabel, long farmhandId)
    {
        string farmhandName = Game1.GetPlayer(farmhandId)?.Name ?? farmhandId.ToString();

        this.Monitor.Log($"Rejected request from {farmhandName} to {actionLabel}, because you disabled that in the mod options.", LogLevel.Info);

        this.Helper.Multiplayer.SendMessage(new RequestDeniedMessage(), nameof(RequestDeniedMessage), modIDs: [this.ModManifest.UniqueID], playerIDs: [farmhandId]);
    }
}
