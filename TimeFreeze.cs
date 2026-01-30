using Omegasis.TimeFreeze.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace Omegasis.TimeFreeze
{
    /// <summary>The mod entry point.</summary>
    public class TimeFreeze : Mod
    {
        /*********
        ** Fields
        *********/
        /// <summary>The mod configuration.</summary>
        private ModConfig Config;

        /// <summary>Whether a cutscene/event was active on the previous tick.</summary>
        private bool wasInCutscene = false;

        /// <summary>Whether time was frozen before the cutscene started.</summary>
        private bool wasTimeFrozenBeforeCutscene = false;

        /// <summary>The tick count when the cutscene ended. Used for delay buffer.</summary>
        private uint cutsceneEndedAtTick = 0;

        /// <summary>Whether we're in the post-cutscene delay period.</summary>
        private bool inPostCutsceneDelay = false;

        /// <summary>Number of ticks to wait after a cutscene ends before re-freezing time (~1 second).</summary>
        private const uint PostCutsceneDelayTicks = 60;

        /// <summary>The saved gameTimeInterval value from before the game update. Used to restore it after the update.</summary>
        private int savedGameTimeInterval = 0;

        /// <summary>Whether time should be frozen this tick. Set in UpdateTicking, used in UpdateTicked.</summary>
        private bool freezeTimeThisTick = false;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            this.Config = helper.ReadConfig<ModConfig>();
            helper.Events.GameLoop.UpdateTicking += this.OnUpdateTicking;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.SaveLoaded += this.GameLoop_SaveLoaded;
        }

        private void GameLoop_SaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            foreach (GameLocation loc in Game1.locations)
            {
                if (this.Config.freezeTimeInThisLocation.ContainsKey(loc.Name) == false)
                {
                    if (loc.IsOutdoors)
                    {
                        this.Config.freezeTimeInThisLocation.Add(loc.Name, false);
                    }
                    else
                    {
                        this.Config.freezeTimeInThisLocation.Add(loc.Name, true);
                    }
                }
            }

            //Patch in the underground mine shaft.
            if (this.Config.freezeTimeInThisLocation.ContainsKey("UndergroundMine") == false)
            {
                this.Config.freezeTimeInThisLocation.Add("UndergroundMine", true);
            }

            this.Helper.WriteConfig<ModConfig>(this.Config);

            // Reset cutscene tracking state on save load
            this.wasInCutscene = false;
            this.wasTimeFrozenBeforeCutscene = false;
            this.cutsceneEndedAtTick = 0;
            this.inPostCutsceneDelay = false;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Check if a cutscene, event, or other non-interactive sequence is currently playing.</summary>
        /// <remarks>
        /// This method detects various game states where time should pass normally, including:
        /// - Standard events/cutscenes (vanilla and modded, e.g., SVE, Ridgeside Village, East Scarp)
        /// - Festivals
        /// - Mini-games (fishing, arcade games, etc.)
        /// - Screen transitions and warps
        /// - Movie theater sequences
        /// - Dialogue sequences that are part of events
        /// All mods that add events use the standard Stardew Valley event system, so this catches them all.
        /// </remarks>
        private bool IsCutscenePlaying()
        {
            // Festivals allow free movement between locations (e.g., the Desert
            // Festival lets you enter Skull Cavern), so they are NOT cutscenes.
            // Time freeze during festivals is handled by normal location-based config.
            // This check must come before eventUp/CurrentEvent because festivals
            // set both of those flags.
            if (Game1.isFestival())
                return false;

            // Standard event/cutscene detection - catches ALL mod events (SVE, RSV, East Scarp, etc.)
            if (Game1.eventUp)
                return true;

            if (Game1.CurrentEvent != null)
                return true;

            // Mini-game detection (fishing game, arcade games, Junimo Kart, etc.)
            if (Game1.currentMinigame != null)
                return true;

            // Screen transition/warp detection
            if (Game1.isWarping)
                return true;

            // Global fade detection (screen fading in/out during transitions)
            if (Game1.globalFade)
                return true;

            // Fade to black detection
            if (Game1.fadeToBlack)
                return true;

            // Movie theater - check if watching a movie
            if (Game1.currentLocation is MovieTheater movieTheater)
            {
                // Check if the movie is currently playing via reflection or state
                // The MovieTheater has an internal state for movie playback
                if (Game1.activeClickableMenu != null &&
                    Game1.activeClickableMenu.GetType().Name.Contains("Movie"))
                    return true;
            }

            // Check for specific event-related menus
            if (Game1.activeClickableMenu != null)
            {
                var menuType = Game1.activeClickableMenu.GetType();
                var menuName = menuType.Name;

                // Dialogue box during events
                if (menuName == "DialogueBox" && Game1.eventUp)
                    return true;

                // Event command menus
                if (menuName.Contains("Event"))
                    return true;
            }

            return false;
        }

        /// <summary>Check if time should currently be frozen based on player locations.</summary>
        private bool ShouldTimeBeFrozen()
        {
            if (Game1.IsMultiplayer)
            {
                if (this.Config.freezeIfEvenOnePlayerMeetsTimeFreezeConditions)
                {
                    foreach (Farmer farmer in Game1.getOnlineFarmers())
                    {
                        if (this.ShouldFreezeTime(farmer, farmer.currentLocation))
                            return true;
                    }
                    return false;
                }
                else if (this.Config.freezeIfMajorityPlayersMeetsTimeFreezeConditions)
                {
                    int freezeCount = 0;
                    int playerCount = 0;
                    foreach (Farmer farmer in Game1.getOnlineFarmers())
                    {
                        playerCount++;
                        if (this.ShouldFreezeTime(farmer, farmer.currentLocation))
                            freezeCount++;
                    }
                    return freezeCount >= (playerCount / 2);
                }
                else if (this.Config.freezeIfAllPlayersMeetTimeFreezeConditions)
                {
                    int freezeCount = 0;
                    int playerCount = 0;
                    foreach (Farmer farmer in Game1.getOnlineFarmers())
                    {
                        playerCount++;
                        if (this.ShouldFreezeTime(farmer, farmer.currentLocation))
                            freezeCount++;
                    }
                    return freezeCount >= playerCount;
                }
            }
            else
            {
                Farmer player = Game1.player;
                return this.ShouldFreezeTime(player, player.currentLocation);
            }
            return false;
        }

        /// <summary>Raised before the game state is updated (~60 times per second).</summary>
        /// <remarks>
        /// Saves the current gameTimeInterval BEFORE the game increments it.
        /// This allows us to restore it after the update, effectively preventing
        /// time from advancing while keeping gameTimeInterval at a natural non-zero
        /// value. This avoids compatibility issues with mods (like Chatter) that
        /// may depend on game systems behaving normally when gameTimeInterval is
        /// not forcibly zeroed.
        /// </remarks>
        private void OnUpdateTicking(object sender, UpdateTickingEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            this.freezeTimeThisTick = false;

            // Don't freeze during cutscenes or post-cutscene delay
            if (this.Config.UnpauseTimeDuringCutscenes)
            {
                if (this.IsCutscenePlaying())
                    return;
                if (this.inPostCutsceneDelay)
                    return;
            }

            if (Game1.showingEndOfNightStuff)
                return;

            if (this.ShouldTimeBeFrozen())
            {
                this.freezeTimeThisTick = true;
                // Cap at a safe value well below the 7000ms threshold that triggers
                // a 10-minute clock advance, so the game update can't cross it
                this.savedGameTimeInterval = System.Math.Min(Game1.gameTimeInterval, 100);
            }
        }

        /// <summary>Raised after the game state is updated (~60 times per second).</summary>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            bool isInCutscene = this.IsCutscenePlaying();

            // Handle cutscene transitions if the feature is enabled
            if (this.Config.UnpauseTimeDuringCutscenes)
            {
                // Cutscene just started
                if (isInCutscene && !this.wasInCutscene)
                {
                    this.wasTimeFrozenBeforeCutscene = this.ShouldTimeBeFrozen();
                    this.inPostCutsceneDelay = false;
                    this.Monitor.Log($"Cutscene started. Time was {(this.wasTimeFrozenBeforeCutscene ? "frozen" : "not frozen")} before.", LogLevel.Trace);
                }
                // Cutscene just ended - start the delay timer
                else if (!isInCutscene && this.wasInCutscene)
                {
                    this.cutsceneEndedAtTick = e.Ticks;
                    this.inPostCutsceneDelay = true;
                    this.Monitor.Log($"Cutscene ended. Starting {PostCutsceneDelayTicks} tick delay before restoring freeze state.", LogLevel.Trace);
                }

                this.wasInCutscene = isInCutscene;

                // Check if post-cutscene delay has elapsed
                if (this.inPostCutsceneDelay && e.Ticks - this.cutsceneEndedAtTick >= PostCutsceneDelayTicks)
                {
                    this.inPostCutsceneDelay = false;
                    this.Monitor.Log($"Post-cutscene delay complete. Restoring time freeze state: {(this.wasTimeFrozenBeforeCutscene ? "frozen" : "not frozen")}.", LogLevel.Trace);
                }
            }

            // Restore the saved gameTimeInterval to prevent time from advancing.
            // Unlike zeroing the interval (which can signal "time just advanced" to
            // the game and other mods), restoring a small non-zero value keeps game
            // systems in a natural state.
            if (this.freezeTimeThisTick)
            {
                Game1.gameTimeInterval = this.savedGameTimeInterval;
            }
        }

        /// <summary>Get whether time should be frozen for the player at the given location.</summary>
        /// <param name="player">The player to check.</param>
        /// <param name="location">The location to check.</param>
        private bool ShouldFreezeTime(Farmer player, GameLocation location)
        {
            if (Game1.showingEndOfNightStuff) return false;

            // Skull Cavern mine levels (MineShaft with level >= 121) should use
            // the "SkullCave" config, not "UndergroundMine". This ensures that
            // setting "SkullCave" to true covers ALL of Skull Cavern, not just
            // the entrance room.
            if (location is MineShaft shaft)
            {
                if (shaft.mineLevel >= 121 && this.Config.freezeTimeInThisLocation.ContainsKey("SkullCave"))
                    return this.Config.freezeTimeInThisLocation["SkullCave"];
                if (this.Config.freezeTimeInThisLocation.ContainsKey("UndergroundMine"))
                    return this.Config.freezeTimeInThisLocation["UndergroundMine"];
                return true;
            }

            // Skull Cavern entrance room
            if (location.Name.Equals("SkullCave") || location.Name.StartsWith("SkullCave"))
            {
                if (this.Config.freezeTimeInThisLocation.ContainsKey("SkullCave"))
                    return this.Config.freezeTimeInThisLocation["SkullCave"];
                return true;
            }

            // Named location in config
            if (this.Config.freezeTimeInThisLocation.ContainsKey(location.Name))
            {
                if (player.swimming.Value)
                {
                    if (this.Config.PassTimeWhileSwimmingInBathhouse && location is BathHousePool)
                        return false;
                }

                return this.Config.freezeTimeInThisLocation[location.Name];
            }

            // Underground mine fallback (regular mines levels 1-120)
            if (location.NameOrUniqueName.StartsWith("UndergroundMine"))
            {
                if (this.Config.freezeTimeInThisLocation.ContainsKey("UndergroundMine"))
                    return this.Config.freezeTimeInThisLocation["UndergroundMine"];
                return true;
            }

            // Default: freeze indoors, pass time outdoors
            return !location.IsOutdoors;
        }
    }
}
