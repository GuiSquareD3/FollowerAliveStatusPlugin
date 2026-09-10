using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using Turbo.Plugins.Default;

namespace Turbo.Plugins.GuiSquare
{
    /// <summary>
    /// Follower life state, plus a counter of FOLLOWER deaths only.
    ///
    ///   GREEN = follower alive
    ///   RED   = follower dead (down on the ground)
    ///   GREY  = no follower hired, or state unknown (loading, menu, multiplayer, hero dead...)
    ///
    /// The counter is stepped up ONLY on a confirmed "alive -> dead" transition of the follower.
    /// Your own death, loading screens and zone changes never count.
    /// </summary>

    public enum FollowerStatusIconPosition
    {
        UnderPortrait,          // below the hero portrait (top left)
        LeftOfHealthGlobe,      // left of the health globe (bottom left)
        RightOfResourceGlobe,   // right of the resource globe (bottom right)
        AboveSkillBar,          // above the skill bar (bottom centre)
        UnderMinimapClock,      // below the minimap clock (top right)
        BelowMinimapText,       // below the tracker text drawn from the minimap's top-left corner
        Custom                  // free placement: CustomX / CustomY (screen ratio 0..1)
    }

    public enum FollowerStatusIconStyle
    {
        Skull,      // skull tinted with the state colour
        Dot,        // plain filled circle
        Portrait    // follower's face plus a small state dot
    }

    public enum FollowerLifeState
    {
        Unknown,      // grey: nothing confirmed yet (loading, transition, hero dead)
        NoFollower,   // grey: no follower hired (or multiplayer game)
        Alive,        // green
        Dead          // red
    }

    public class FollowerAliveStatusPlugin : BasePlugin, IInGameTopPainter, IAfterCollectHandler, INewAreaHandler, IMouseClickHandler, IMouseClickBlocker
    {
        // ---------------------------------------------------------------------
        // Display settings
        // ---------------------------------------------------------------------

        public FollowerStatusIconPosition Position { get; set; }

        /// <summary>Free placement, as a screen ratio (0..1). Only used when Position == Custom.</summary>
        public float CustomX { get; set; }
        public float CustomY { get; set; }

        /// <summary>Extra offset applied to every position, as a ratio of screen height.</summary>
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        /// <summary>Icon size, as a ratio of screen height (0.024 = about 26px at 1080p).</summary>
        public float IconSizeRatio { get; set; }

        public bool ShowIcon { get; set; }
        public bool ShowCounter { get; set; }
        /// <summary>true = counter to the right of the icon, false = counter inside the icon.</summary>
        public bool CounterOnRight { get; set; }
        /// <summary>Hide the counter while it is still zero.</summary>
        public bool HideCounterWhenZero { get; set; }

        /// <summary>
        /// Prefix drawn in front of the counter, so a lone digit is not ambiguous.
        /// Default: "\u00D7" (multiplication sign), which reads as "x2".
        /// Set to "" to show the bare number.
        /// </summary>
        public string CounterPrefix { get; set; }

        /// <summary>Icon appearance: skull, plain dot, or the follower's portrait.</summary>
        public FollowerStatusIconStyle IconStyle { get; set; }

        /// <summary>
        /// Number of text lines already drawn over the minimap. This is what actually
        /// sets the drop below the minimap's top edge, because that block's height is
        /// lineCount * lineHeight, and lineHeight is measured live (see below) rather
        /// than assumed. Count the lines you actually see and set this, adding one for
        /// breathing room. Set it to 0 to fall back on MinimapTextHeightRatio instead.
        ///
        /// Why not a simple fraction of the minimap: that text is sized so its widest
        /// line fits the minimap width, but stops growing once a glyph reaches
        /// MinLineHeight AND the size reaches its own cap. That floor is in absolute
        /// pixels, so on a small window the text takes a LARGER share of the minimap
        /// than on a big one. A fixed fraction cannot track that; a measured line
        /// height can.
        /// </summary>
        public int MinimapTextLineCount { get; set; }

        /// <summary>
        /// Absolute floor, in pixels, for a line of the text drawn over the minimap.
        /// That text refuses to be smaller than this however small the window gets.
        /// </summary>
        public float MinLineHeight { get; set; }

        /// <summary>
        /// Grow the counter and the probe font past their nominal size until a line
        /// clears MinLineHeight, the way the text over the minimap does. false draws at
        /// the nominal size, which goes visibly small at a low resolution.
        /// </summary>
        public bool AutoFontSize { get; set; }

        /// <summary>Nominal size of the text drawn over the minimap, before the floor applies.</summary>
        public float TrackerTextSize { get; set; }

        /// <summary>Nominal size of the death counter, before the floor applies.</summary>
        public float CounterFontSize { get; set; }

        /// <summary>
        /// Height of that text block, as a fraction of the minimap's height.
        /// Only used by Position = BelowMinimapText, and only when
        /// MinimapTextLineCount is 0. Does not survive a window resize: prefer the line
        /// count above.
        /// </summary>
        public float MinimapTextHeightRatio { get; set; }

        /// <summary>Pulse the icon while the follower is dead.</summary>
        public bool BlinkWhenDead { get; set; }
        /// <summary>Hide the icon while the world map or act map is open.</summary>
        public bool HideOnMapModes { get; set; }
        /// <summary>Hide the icon entirely when no follower is hired, instead of showing grey.</summary>
        public bool HideWhenNoFollower { get; set; }

        public IBrush AliveBrush { get; set; }
        public IBrush DeadBrush { get; set; }
        public IBrush NoFollowerBrush { get; set; }
        public IBrush UnknownBrush { get; set; }
        public IBrush BackgroundBrush { get; set; }
        public IBrush BorderBrush { get; set; }
        /// <summary>Brush for the skull's hollows (eye sockets, nose, teeth).</summary>
        public IBrush IconDetailBrush { get; set; }
        public IFont CounterFont { get; set; }
        public IFont CounterDeadFont { get; set; }
        public IFont DebugFont { get; set; }

        // ---------------------------------------------------------------------
        // Detection settings (false-positive guards)
        // ---------------------------------------------------------------------

        /// <summary>SNOs of the actually hired hirelings (not the town NPC versions).</summary>
        public HashSet<ActorSnoEnum> FollowerActorSnos { get; set; }

        /// <summary>true (default): accept ONLY the SNOs above. false: also accept any ActorKind.Follower actor.</summary>
        public bool StrictSnoMatch { get; set; }

        /// <summary>Treat the follower actor vanishing (in the same world) as a death.</summary>
        public bool TreatVanishAsDeath { get; set; }

        /// <summary>Delay before concluding a death once the actor has vanished (ms).</summary>
        public int VanishGraceMs { get; set; }

        /// <summary>Blind window after a zone change or a new game (ms).</summary>
        public int AreaChangeGraceMs { get; set; }

        /// <summary>Blind window after the hero's death or resurrection (ms).</summary>
        public int PlayerDeathGraceMs { get; set; }

        /// <summary>Detect nothing at all while the hero is dead.</summary>
        public bool IgnoreWhilePlayerDead { get; set; }

        /// <summary>
        /// Reset the counter on every new game.
        /// false (default): the counter survives quitting to the menu and starting another
        /// game; it only goes back to zero when TurboHUD restarts, or on a reset click.
        /// </summary>
        public bool ResetCounterOnNewGame { get; set; }

        /// <summary>Allow resetting the counter by clicking the icon.</summary>
        public bool ResetCounterOnClick { get; set; }

        /// <summary>
        /// Mouse button used for the reset. Default: left.
        /// Set to MouseButtons.Middle for a modifier-free gesture with no risk of a
        /// misclick, since the middle button is not bound to movement in Diablo III.
        /// </summary>
        public MouseButtons ResetClickButton { get; set; }

        /// <summary>
        /// Require Ctrl in addition to the click. true by default, because the icon sits
        /// in the top-left area where you click to move, and an accidental reset would be
        /// unrecoverable.
        ///
        /// Set it to false for a plain click:
        ///     Hud.RunOnPlugin&lt;FollowerAliveStatusPlugin&gt;(p =&gt; p.ResetClickRequiresCtrl = false);
        ///
        /// The hover tooltip states whichever gesture is currently configured, and the
        /// click is swallowed either way so the character never moves under the icon.
        /// </summary>
        public bool ResetClickRequiresCtrl { get; set; }

        /// <summary>Spoken alert on every follower death.</summary>
        public bool SpeakOnDeath { get; set; }
        public string SpeakOnDeathText { get; set; }

        /// <summary>Show the diagnostic panel (turn it on once to validate in game).</summary>
        public bool DebugEnabled { get; set; }
        public float DebugX { get; set; }
        public float DebugY { get; set; }

        // ---------------------------------------------------------------------
        // Exposed state (read-only in practice)
        // ---------------------------------------------------------------------

        public FollowerLifeState State { get; private set; }
        public int DeathCount { get; set; }
        public ActorSnoEnum CurrentFollowerSno { get; private set; }
        public bool Armed { get; private set; } // the follower was seen alive => a death may now be counted

        // ---------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------

        private DateTime _blockUntil;      // blind window (transition / hero death)
        private DateTime _lastSeenUtc;     // last time the follower actor was found
        private DateTime _lastAliveUtc;    // last time it was seen alive
        private DateTime _lastDeathUtc;
        private uint _lastFollowerWorldId;
        private bool _playerWasDead;
        private bool _dbgHired;
        private readonly List<DateTime> _deathTimes = new List<DateTime>();

        // Clickable area of the icon. Written by the render thread, read by the click
        // blocker thread (IMouseClickBlocker), so everything goes through this lock.
        private readonly object _hitLock = new object();
        private bool _hitValid;
        private float _hitX, _hitY, _hitW, _hitH;

        // diagnostics
        private string _dbgHp = "-";
        private string _dbgHpLine = "-";
        private string _dbgFlags = "-";
        private string _dbgGear = "-";
        private string _dbgKindActors = "-";
        private uint _hpTrackedAcd;
        private float _hpObservedMin;
        private float _hpObservedMax;

        // Auto-sizing, mirroring how the text over the minimap sizes itself.
        private const float FontSizeCeiling = 40.0f;
        private const float FontSizeStep = 0.1f;
        private IFont _lineProbeFont;
        private float _lastWindowHeight = -1f;
        private string _dbgFontSizes = "-";
        private float _dbgLineHeight;

        private ITexture _templarTexture;
        private ITexture _scoundrelTexture;
        private ITexture _enchantressTexture;

        public FollowerAliveStatusPlugin()
        {
            Enabled = true;
            Order = 30000;

            Position = FollowerStatusIconPosition.BelowMinimapText;
            // 7 tracker lines, one for breathing room, then the three lines of the Greater
            // Rift boss timer. Drop this back to 8 if that timer is not running.
            MinimapTextLineCount = 11;
            MinLineHeight = 13.5f;  // that text's own floor, in absolute pixels
            AutoFontSize = true;
            TrackerTextSize = 8.0f;
            CounterFontSize = 7.5f;
            MinimapTextHeightRatio = 0.46f;    // legacy fallback, only when MinimapTextLineCount == 0
            IconStyle = FollowerStatusIconStyle.Skull;
            CustomX = 0.5f;
            CustomY = 0.92f;
            OffsetX = 0f;
            OffsetY = 0f;
            IconSizeRatio = 0.024f;

            ShowIcon = true;
            ShowCounter = true;
            CounterOnRight = true;
            HideCounterWhenZero = false;
            CounterPrefix = "\u00D7"; // escaped so this source file stays pure ASCII
            BlinkWhenDead = true;
            HideOnMapModes = true;
            HideWhenNoFollower = false;

            StrictSnoMatch = true;
            // Death is detected reliably through hitpoints (IActor.Hitpoints drops to 0
            // while the actor stays present), so the vanish heuristic is not needed.
            TreatVanishAsDeath = false;
            VanishGraceMs = 2500;
            // Blind windows are deliberately short. The real guard against miscounting is
            // the Armed latch (a death requires a prior "alive" confirmation), which is
            // reset by OnNewArea and by resurrection. These delays now only cover the few
            // frames where an actor still initialising may report 0 hitpoints. Making them
            // longer would silently drop genuine deaths happening right after a transition.
            AreaChangeGraceMs = 1500;
            PlayerDeathGraceMs = 1000;
            IgnoreWhilePlayerDead = true;
            ResetCounterOnNewGame = false;
            ResetCounterOnClick = true;
            ResetClickButton = MouseButtons.Left;
            ResetClickRequiresCtrl = true;

            SpeakOnDeath = false;
            SpeakOnDeathText = "Follower down";

            DebugEnabled = false;
            DebugX = 0.02f;
            DebugY = 0.30f;

            State = FollowerLifeState.Unknown;
            DeathCount = 0;
            Armed = false;
            _lastFollowerWorldId = uint.MaxValue;
        }

        public override void Load(IController hud)
        {
            base.Load(hud);

            FollowerActorSnos = new HashSet<ActorSnoEnum>
            {
                ActorSnoEnum._hireling_templar,        // 52693
                ActorSnoEnum._hireling_scoundrel,      // 52694
                ActorSnoEnum._hireling_enchantress,    // 4482
            };

            AliveBrush      = Hud.Render.CreateBrush(230,  40, 200,  70, 0);
            DeadBrush       = Hud.Render.CreateBrush(240, 225,  35,  35, 0);
            NoFollowerBrush = Hud.Render.CreateBrush(170, 115, 115, 115, 0);
            UnknownBrush    = Hud.Render.CreateBrush(170, 115, 115, 115, 0);
            BackgroundBrush = Hud.Render.CreateBrush(160,   0,   0,   0, 0);
            BorderBrush     = Hud.Render.CreateBrush(220,  15,  15,  15, 1.4f);
            IconDetailBrush = Hud.Render.CreateBrush(255,  10,  10,  12, 0);

            DebugFont       = Hud.Render.CreateFont("consolas", 8.5f, 255, 255, 255, 160, false, false, 200, 0, 0, 0, true);

            // The counter and the probe font are built by EnsureFonts instead, at a size
            // derived from the window. These are only the fall-backs for the first frame
            // and for AutoFontSize = false.
            CounterFont     = Hud.Render.CreateFont("tahoma", CounterFontSize, 255, 240, 240, 240, true, false, 190, 0, 0, 0, true);
            CounterDeadFont = Hud.Render.CreateFont("tahoma", CounterFontSize, 255, 255, 105, 105, true, false, 190, 0, 0, 0, true);

            // Probe font matching the text drawn over the minimap. We never draw with
            // it: we only measure a line's height, which is what tells us how far down
            // that text actually reaches right now.
            _lineProbeFont = Hud.Render.CreateFont("Arial", TrackerTextSize, 255, 255, 255, 255, true, false, false);

            _templarTexture     = Hud.Texture.GetTexture(3116868919u);
            _scoundrelTexture   = Hud.Texture.GetTexture(441912908u);
            _enchantressTexture = Hud.Texture.GetTexture(2807221403u);
        }

        // =====================================================================
        // Detection (collection phase, no rendering here)
        // =====================================================================

        public void OnNewArea(bool newGame, ISnoArea area)
        {
            var now = Hud.Time.Now;

            // any zone transition: disarm, and stay blind for a moment
            _blockUntil = now.AddMilliseconds(AreaChangeGraceMs);
            Armed = false;
            _lastSeenUtc = DateTime.MinValue;
            _lastAliveUtc = DateTime.MinValue;
            _lastFollowerWorldId = uint.MaxValue;

            if (newGame)
            {
                State = FollowerLifeState.Unknown;
                CurrentFollowerSno = default(ActorSnoEnum);
                if (ResetCounterOnNewGame)
                {
                    DeathCount = 0;
                    _deathTimes.Clear();
                }
            }
        }

        public void AfterCollect()
        {
            if (!Enabled)
                return;

            var now = Hud.Time.Now;

            // --- 1) out of game / loading / menu --------------------------------
            var me = Hud.Game.Me;
            if (!Hud.Game.IsInGame || Hud.Game.IsLoading || me == null || !me.IsInGame || !me.HasValidActor)
            {
                _blockUntil = now.AddMilliseconds(AreaChangeGraceMs);
                Armed = false;
                _dbgHired = false;
                State = FollowerLifeState.Unknown;
                _lastSeenUtc = DateTime.MinValue;
                _dbgHp = "-";
                _dbgHpLine = "-";
                _dbgFlags = "-";
                _dbgGear = "-";
                _dbgKindActors = "-";
                return;
            }

            // In diagnostic mode, read the raw signals BEFORE every filter, so the
            // follower can still be observed while the hero is dead or the game is paused.
            if (DebugEnabled)
                CollectDebugInfo(FindFollowerActor());

            // --- 1b) game paused (ESC menu): actor data is no longer trustworthy -
            if (Hud.Game.IsPaused)
            {
                _blockUntil = now.AddMilliseconds(AreaChangeGraceMs);
                return;
            }

            // --- 2) multiplayer game: no follower is possible -------------------
            if (Hud.Game.NumberOfPlayersInGame > 1)
            {
                Armed = false;
                State = FollowerLifeState.NoFollower;
                CurrentFollowerSno = default(ActorSnoEnum);
                return;
            }

            // --- 3) the hero is dead: freeze everything -------------------------
            var playerDead = me.IsDeadSafeCheck || me.IsDead;
            if (playerDead)
            {
                _playerWasDead = true;
                _blockUntil = now.AddMilliseconds(PlayerDeathGraceMs);
                if (IgnoreWhilePlayerDead)
                    return; // state frozen, never counted
            }
            else if (_playerWasDead)
            {
                // just resurrected: extend the blind window
                _playerWasDead = false;
                _blockUntil = now.AddMilliseconds(PlayerDeathGraceMs);
                Armed = false;
            }

            // --- 4) look for the follower ---------------------------------------
            var follower = FindFollowerActor();
            var hiredByGear = FollowerGearEquipped();

            var hired = (follower != null) || hiredByGear;
            _dbgHired = hired;

            if (!hired)
            {
                Armed = false;
                State = FollowerLifeState.NoFollower;
                CurrentFollowerSno = default(ActorSnoEnum);
                return;
            }

            // --- 5) the actor is present: source of truth ------------------------
            if (follower != null)
            {
                CurrentFollowerSno = follower.SnoActor.Sno;
                _lastSeenUtc = now;
                _lastFollowerWorldId = follower.WorldId;

                if (IsActorAlive(follower))
                {
                    _lastAliveUtc = now;
                    Armed = true;
                    State = FollowerLifeState.Alive;
                }
                else
                {
                    RegisterDeath(now);
                }
                return;
            }

            // --- 6) hired, but the actor is missing ------------------------------
            if (!TreatVanishAsDeath)
            {
                // Hitpoint detection is enough (see IsActorAlive): a missing actor is only
                // a collection gap. Keep the last known state, unless nothing was ever
                // confirmed, in which case fall back to grey.
                if (State != FollowerLifeState.Alive && State != FollowerLifeState.Dead)
                    State = FollowerLifeState.Unknown;
                return;
            }

            if (!Armed)
            {
                // never confirmed alive in this area: conclude nothing
                if (State != FollowerLifeState.Dead)
                    State = FollowerLifeState.Unknown;
                return;
            }

            if (me.WorldId != _lastFollowerWorldId)
                return; // we changed world: the disappearance is expected

            if ((now - _lastSeenUtc).TotalMilliseconds < VanishGraceMs)
                return; // too early to conclude

            RegisterDeath(now);
        }

        private void RegisterDeath(DateTime now)
        {
            if (now < _blockUntil)
                return; // zone transition / hero death: conclude nothing

            if (State == FollowerLifeState.Dead)
                return; // already counted

            State = FollowerLifeState.Dead;
            _lastDeathUtc = now;

            if (Armed)
            {
                DeathCount++;
                _deathTimes.Add(now);
                if (_deathTimes.Count > 5)
                    _deathTimes.RemoveAt(0);
                if (SpeakOnDeath)
                    Hud.Sound.Speak(SpeakOnDeathText);
                if (DebugEnabled)
                    Hud.Debug("[FollowerAliveStatus] follower death #" + DeathCount.ToString(CultureInfo.InvariantCulture));
            }

            Armed = false; // it must be seen alive again before another death can be counted
        }

        private IActor FindFollowerActor()
        {
            IActor byKind = null;

            foreach (var actor in Hud.Game.Actors)
            {
                var sno = actor.SnoActor;
                if (sno == null)
                    continue;

                if (FollowerActorSnos.Contains(sno.Sno))
                    return actor;

                if (!StrictSnoMatch && byKind == null && sno.Kind == ActorKind.Follower)
                    byKind = actor;
            }

            return byKind;
        }

        private bool IsActorAlive(IActor actor)
        {
            // Primary signal: IActor.Hitpoints, populated by TurboHUD itself.
            // (the Hitpoints_Cur attribute is NOT exposed on follower actors: it returns -1)
            if (actor.Hitpoints > 0.0001f)
                return true;

            // Secondary signal: if the attribute is readable and positive, treat as alive.
            // Death is concluded only when both sources agree.
            var hpAttr = actor.GetAttributeValue(Hud.Sno.Attributes.Hitpoints_Cur, 0, -1.0d);
            if (hpAttr > 0.0001d)
                return true;

            return false;
        }

        private bool FollowerGearEquipped()
        {
            foreach (var item in Hud.Game.Items)
            {
                var loc = item.Location;
                if (loc >= ItemLocation.PetRightHand && loc <= ItemLocation.PetBracers)
                    return true;
            }
            return false;
        }

        // =====================================================================
        // Rendering
        // =====================================================================

        public void PaintTopInGame(ClipState clipState)
        {
            if (clipState != ClipState.BeforeClip)
                return; // only touch the clickable area on the main render pass

            if (!Enabled || Hud.Render.UiHidden || !Hud.Game.IsInGame)
            {
                ClearHitArea();
                return;
            }
            if (HideOnMapModes && (Hud.Game.MapMode == MapMode.WaypointMap || Hud.Game.MapMode == MapMode.ActMap))
            {
                ClearHitArea();
                return;
            }

            // Before anything is drawn or measured: the counter and the probe font
            // follow the window size, the way the text over the minimap does.
            EnsureFonts();

            if (DebugEnabled)
                PaintDebug();

            var noFollower = State == FollowerLifeState.NoFollower;
            if (HideWhenNoFollower && noFollower)
            {
                ClearHitArea();
                return;
            }

            float x, y, size;
            GetIconRect(out x, out y, out size);
            if (size <= 0f)
            {
                ClearHitArea();
                return;
            }

            var cx = x + (size / 2f);
            var cy = y + (size / 2f);
            var r = size / 2f;

            var brush = GetStateBrush();
            var opacity = 1.0f;

            if (BlinkWhenDead && State == FollowerLifeState.Dead)
            {
                // pulse, about 1.1s per cycle
                var phase = (Hud.Game.CurrentRealTimeMilliseconds % 1100L) / 1100.0d;
                opacity = (float)(0.45d + (0.55d * Math.Abs(Math.Cos(phase * Math.PI))));
            }

            if (ShowIcon && brush != null)
            {
                switch (IconStyle)
                {
                    case FollowerStatusIconStyle.Skull:
                        DrawSkull(x, y, size, brush, opacity);
                        break;

                    case FollowerStatusIconStyle.Portrait:
                        if (BackgroundBrush != null)
                            BackgroundBrush.DrawEllipse(cx, cy, r * 1.18f, r * 1.18f);
                        var tex = GetFollowerTexture();
                        if (tex != null)
                            tex.Draw(x + (size * 0.08f), y + (size * 0.08f), size * 0.84f, size * 0.84f, 0.9f);
                        WithOpacity(brush, opacity, () =>
                            brush.DrawEllipse(x + (size * 0.82f), y + (size * 0.82f), r * 0.42f, r * 0.42f));
                        if (BorderBrush != null)
                            BorderBrush.DrawEllipse(cx, cy, r, r);
                        break;

                    default: // Dot
                        if (BackgroundBrush != null)
                            BackgroundBrush.DrawEllipse(cx, cy, r * 1.18f, r * 1.18f);
                        WithOpacity(brush, opacity, () => brush.DrawEllipse(cx, cy, r, r));
                        if (BorderBrush != null)
                            BorderBrush.DrawEllipse(cx, cy, r, r);
                        break;
                }
            }

            if (ShowCounter && !(HideCounterWhenZero && DeathCount == 0))
            {
                var font = State == FollowerLifeState.Dead ? CounterDeadFont : CounterFont;
                if (font != null)
                {
                    var text = CounterPrefix + DeathCount.ToString(CultureInfo.InvariantCulture);
                    var layout = font.GetTextLayout(text);
                    if (CounterOnRight || !ShowIcon)
                        font.DrawText(layout, x + (ShowIcon ? size * 1.15f : 0f), cy - (layout.Metrics.Height / 2f));
                    else
                        font.DrawText(layout, cx - (layout.Metrics.Width / 2f), cy - (layout.Metrics.Height / 2f));
                }
            }

            // hover and click area
            var hoverW = size * (ShowCounter && CounterOnRight ? 2.4f : 1.0f);
            SetHitArea(x, y, hoverW, size);

            if (Hud.Window.CursorInsideRect(x, y, hoverW, size))
                Hud.Render.SetHint(BuildHint());
        }

        // =====================================================================
        // Resetting the counter by clicking the icon
        // =====================================================================

        private void SetHitArea(float x, float y, float w, float h)
        {
            lock (_hitLock)
            {
                _hitValid = true;
                _hitX = x; _hitY = y; _hitW = w; _hitH = h;
            }
        }

        private void ClearHitArea()
        {
            lock (_hitLock)
            {
                _hitValid = false;
            }
        }

        private bool PointIsOnIcon(float px, float py)
        {
            lock (_hitLock)
            {
                return _hitValid
                    && px >= _hitX && px <= _hitX + _hitW
                    && py >= _hitY && py <= _hitY + _hitH;
            }
        }

        private bool ResetGestureActive(float px, float py, MouseButtons button)
        {
            if (!Enabled || !ResetCounterOnClick)
                return false;
            if (button != ResetClickButton)
                return false;
            if (ResetClickRequiresCtrl && !Hud.Input.IsKeyDown(Keys.ControlKey))
                return false;
            return PointIsOnIcon(px, py);
        }

        /// <summary>
        /// Called on a separate thread: swallow the click so it never reaches the game and
        /// the character does not walk to wherever the icon sits.
        /// </summary>
        public bool MouseClickShouldBeBlocked(MouseButtons button, int x, int y)
        {
            return ResetGestureActive(x, y, button);
        }

        public bool MouseDown(MouseButtons button)
        {
            if (!ResetGestureActive(Hud.Window.CursorX, Hud.Window.CursorY, button))
                return false;

            DeathCount = 0;
            _deathTimes.Clear();
            return true;
        }

        public bool MouseUp(MouseButtons button)
        {
            // also consume the release of the click handled in MouseDown
            return ResetGestureActive(Hud.Window.CursorX, Hud.Window.CursorY, button);
        }

        private static void WithOpacity(IBrush brush, float opacity, Action draw)
        {
            var previous = brush.Opacity;
            brush.Opacity = opacity;
            draw();
            brush.Opacity = previous;
        }

        /// <summary>
        /// Skull drawn from primitives: cranium and jaw in the state colour, eye sockets,
        /// nose and teeth hollowed out in near-black. Same footprint as the plain dot.
        /// </summary>
        private void DrawSkull(float x, float y, float s, IBrush body, float opacity)
        {
            if (BackgroundBrush != null)
                BackgroundBrush.DrawEllipse(x + (s * 0.5f), y + (s * 0.5f), s * 0.59f, s * 0.59f);

            WithOpacity(body, opacity, () =>
            {
                body.DrawEllipse(x + (s * 0.50f), y + (s * 0.42f), s * 0.40f, s * 0.36f); // cranium
                body.DrawEllipse(x + (s * 0.50f), y + (s * 0.70f), s * 0.26f, s * 0.22f); // jaw
            });

            var hole = IconDetailBrush;
            if (hole == null)
                return;

            WithOpacity(hole, opacity, () =>
            {
                hole.DrawEllipse(x + (s * 0.325f), y + (s * 0.40f), s * 0.140f, s * 0.150f); // left eye socket
                hole.DrawEllipse(x + (s * 0.675f), y + (s * 0.40f), s * 0.140f, s * 0.150f); // right eye socket
                hole.DrawEllipse(x + (s * 0.500f), y + (s * 0.585f), s * 0.055f, s * 0.085f); // nose

                // teeth
                hole.DrawRectangle(x + (s * 0.400f), y + (s * 0.670f), s * 0.032f, s * 0.135f);
                hole.DrawRectangle(x + (s * 0.484f), y + (s * 0.670f), s * 0.032f, s * 0.135f);
                hole.DrawRectangle(x + (s * 0.568f), y + (s * 0.670f), s * 0.032f, s * 0.135f);
            });
        }

        private IBrush GetStateBrush()
        {
            switch (State)
            {
                case FollowerLifeState.Alive: return AliveBrush;
                case FollowerLifeState.Dead: return DeadBrush;
                case FollowerLifeState.NoFollower: return NoFollowerBrush;
                default: return UnknownBrush;
            }
        }

        private ITexture GetFollowerTexture()
        {
            if (CurrentFollowerSno == ActorSnoEnum._hireling_templar) return _templarTexture;
            if (CurrentFollowerSno == ActorSnoEnum._hireling_scoundrel) return _scoundrelTexture;
            if (CurrentFollowerSno == ActorSnoEnum._hireling_enchantress) return _enchantressTexture;
            return null;
        }

        private string BuildHint()
        {
            string s;
            switch (State)
            {
                case FollowerLifeState.Alive: s = "Follower alive"; break;
                case FollowerLifeState.Dead: s = "Follower DEAD"; break;
                case FollowerLifeState.NoFollower: s = "No follower hired"; break;
                default: s = "Unknown state"; break;
            }
            s += " - follower deaths: " + DeathCount.ToString(CultureInfo.InvariantCulture);

            if (ResetCounterOnClick)
            {
                s += ResetClickRequiresCtrl
                    ? " (ctrl+click to reset)"
                    : " (click to reset)";
            }

            return s;
        }

        private void GetIconRect(out float x, out float y, out float size)
        {
            var w = (float)Hud.Window.Size.Width;
            var h = (float)Hud.Window.Size.Height;

            size = h * IconSizeRatio;
            x = w * CustomX;
            y = h * CustomY;

            System.Drawing.RectangleF rect;

            switch (Position)
            {
                case FollowerStatusIconPosition.UnderPortrait:
                    if (TryGetRect("*portrait-bottom", out rect))
                    {
                        x = rect.Left + (rect.Width * 0.06f);
                        y = rect.Bottom + (size * 0.35f);
                    }
                    break;

                case FollowerStatusIconPosition.LeftOfHealthGlobe:
                    if (TryGetRect("Root.NormalLayer.game_dialog_backgroundScreenPC.game_progressBar_healthBall", out rect))
                    {
                        x = rect.Left - (size * 1.35f);
                        y = rect.Top + (rect.Height * 0.18f);
                    }
                    break;

                case FollowerStatusIconPosition.RightOfResourceGlobe:
                    if (TryGetRect("Root.NormalLayer.game_dialog_backgroundScreenPC.game_progressBar_manaBall", out rect))
                    {
                        x = rect.Right + (size * 0.35f);
                        y = rect.Top + (rect.Height * 0.18f);
                    }
                    break;

                case FollowerStatusIconPosition.AboveSkillBar:
                    var bottom = Hud.Render.InGameBottomHudUiElement;
                    if (bottom != null && bottom.Rectangle.Width > 0f)
                    {
                        rect = bottom.Rectangle;
                        x = rect.Left + (rect.Width * 0.5f) - (size * 0.5f);
                        y = rect.Top - (size * 1.35f);
                    }
                    break;

                case FollowerStatusIconPosition.BelowMinimapText:
                    // The tracker text is drawn from the minimap's top-left corner
                    // (Hud.Render.MinimapUiElement) downwards. We sit below it.
                    var minimap = Hud.Render.MinimapUiElement;
                    if (minimap != null && minimap.Rectangle.Width > 0f)
                    {
                        rect = minimap.Rectangle;
                        x = rect.Left;

                        if (MinimapTextLineCount > 0)
                        {
                            // Measured every frame, so the icon follows a window resize.
                            var lineHeight = MeasureLineHeight();
                            y = rect.Top + (MinimapTextLineCount * lineHeight);
                        }
                        else
                        {
                            y = rect.Top + (rect.Height * MinimapTextHeightRatio);
                        }
                    }
                    break;

                case FollowerStatusIconPosition.UnderMinimapClock:
                    if (TryGetRect("Root.NormalLayer.minimap_dialog_backgroundScreen.minimap_dialog_pve.BoostWrapper.BoostsDifficultyStackPanel.clock", out rect))
                    {
                        x = rect.Left;
                        y = rect.Bottom + (size * 0.35f);
                    }
                    break;
            }

            x += h * OffsetX;
            y += h * OffsetY;
        }

        /// <summary>
        /// Height of one line of the text drawn over the minimap, in pixels, at the
        /// current window size. Measured from a probe font sized by the same rule that
        /// text uses (see ResolveFontSize), so the drop below it stays true on a resize.
        /// </summary>
        private float MeasureLineHeight()
        {
            EnsureFonts();

            var h = MinLineHeight;

            if (_lineProbeFont != null)
            {
                var layout = _lineProbeFont.GetTextLayout("X");
                if (layout != null && layout.Metrics.Height > h)
                    h = layout.Metrics.Height;
            }

            _dbgLineHeight = h;
            return h;
        }

        /// <summary>
        /// The sizing rule the text drawn over the minimap uses: a nominal size, but
        /// grown past it until a line is at least MinLineHeight pixels tall.
        ///
        /// Both halves matter. TurboHUD font sizes scale with the window, so at a small
        /// resolution the nominal size gives lines well under the floor, and that text
        /// keeps growing until it clears it. Anything drawn at a fixed nominal size
        /// therefore looks right at 1080p and visibly too small at 800x600 -- which is
        /// what the counter beside the icon used to do.
        ///
        /// The width half of that text's own rule (grow until it fills the minimap) is
        /// deliberately left out: the counter is a two or three character label that
        /// never comes close, so it would be a branch that can never fire.
        /// </summary>
        private float ResolveFontSize(string family, float nominalSize, bool bold)
        {
            var size = nominalSize;

            while (size < FontSizeCeiling)
            {
                var probe = Hud.Render.CreateFont(family, size, 255, 255, 255, 255, bold, false, false);
                var layout = probe.GetTextLayout("X");
                if (layout != null && layout.Metrics.Height >= MinLineHeight)
                    break;

                size += FontSizeStep;
            }

            return size;
        }

        /// <summary>
        /// Rebuild the fonts whose size follows the window. Only ever runs after a
        /// resize, so the search costs nothing per frame.
        /// </summary>
        private void EnsureFonts()
        {
            var height = (float)Hud.Window.Size.Height;

            if (_lineProbeFont != null && Math.Abs(height - _lastWindowHeight) < 0.5f)
                return;

            _lastWindowHeight = height;

            if (!AutoFontSize)
            {
                if (_lineProbeFont == null)
                    _lineProbeFont = Hud.Render.CreateFont("Arial", TrackerTextSize, 255, 255, 255, 255, true, false, false);
                return;
            }

            var probeSize = ResolveFontSize("Arial", TrackerTextSize, true);
            _lineProbeFont = Hud.Render.CreateFont("Arial", probeSize, 255, 255, 255, 255, true, false, false);

            var counterSize = ResolveFontSize("tahoma", CounterFontSize, true);
            CounterFont = Hud.Render.CreateFont("tahoma", counterSize, 255, 240, 240, 240, true, false, 190, 0, 0, 0, true);
            CounterDeadFont = Hud.Render.CreateFont("tahoma", counterSize, 255, 255, 105, 105, true, false, 190, 0, 0, 0, true);

            _dbgFontSizes = "probe=" + probeSize.ToString("0.0", CultureInfo.InvariantCulture)
                + " counter=" + counterSize.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private bool TryGetRect(string path, out System.Drawing.RectangleF rect)
        {
            rect = System.Drawing.RectangleF.Empty;
            var el = Hud.Render.GetUiElement(path);
            if (el == null)
                return false;
            rect = el.Rectangle;
            return rect.Width > 0f && rect.Height > 0f;
        }

        // =====================================================================
        // Diagnostics
        // =====================================================================

        private void CollectDebugInfo(IActor follower)
        {
            if (follower == null)
            {
                _dbgHp = "no follower actor";
                _dbgHpLine = "-";
                _dbgFlags = "-";
            }
            else
            {
                _dbgHp = follower.SnoActor.Sno.ToString()
                    + " acd=" + follower.AcdId.ToString(CultureInfo.InvariantCulture)
                    + " world=" + follower.WorldId.ToString(CultureInfo.InvariantCulture);

                // Track the min/max of IActor.Hitpoints: if it varies, it really is current health.
                var hpActor = follower.Hitpoints;
                if (_hpTrackedAcd != follower.AcdId)
                {
                    _hpTrackedAcd = follower.AcdId;
                    _hpObservedMin = hpActor;
                    _hpObservedMax = hpActor;
                }
                else
                {
                    if (hpActor < _hpObservedMin) _hpObservedMin = hpActor;
                    if (hpActor > _hpObservedMax) _hpObservedMax = hpActor;
                }

                var hpAttr = follower.GetAttributeValue(Hud.Sno.Attributes.Hitpoints_Cur, 0, -1.0d);

                _dbgHpLine = "actor=" + hpActor.ToString("0", CultureInfo.InvariantCulture)
                    + " attr=" + hpAttr.ToString("0", CultureInfo.InvariantCulture)
                    + " seen[" + _hpObservedMin.ToString("0", CultureInfo.InvariantCulture)
                    + ".." + _hpObservedMax.ToString("0", CultureInfo.InvariantCulture) + "]";

                _dbgFlags = "untargetable=" + follower.Untargetable
                    + " disabled=" + follower.IsDisabled
                    + " operated=" + follower.IsOperated;
            }

            var gear = 0;
            foreach (var item in Hud.Game.Items)
            {
                var loc = item.Location;
                if (loc >= ItemLocation.PetRightHand && loc <= ItemLocation.PetBracers)
                    gear++;
            }
            _dbgGear = gear.ToString(CultureInfo.InvariantCulture) + " follower item(s) equipped";

            var kinds = "";
            var n = 0;
            foreach (var actor in Hud.Game.Actors)
            {
                var sno = actor.SnoActor;
                if (sno == null || sno.Kind != ActorKind.Follower)
                    continue;
                if (n > 0) kinds += ", ";
                kinds += sno.Sno.ToString();
                n++;
                if (n >= 4) break;
            }
            _dbgKindActors = n == 0 ? "none" : kinds;
        }

        private string BuildDeathLog()
        {
            if (_deathTimes.Count == 0)
                return "-";

            var s = "";
            for (var i = _deathTimes.Count - 1; i >= 0; i--)
            {
                if (s.Length > 0) s += ", ";
                s += _deathTimes[i].ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return s;
        }

        /// <summary>
        /// Window and minimap geometry, so a window resize can be watched live: every
        /// number here must move when the game window is resized.
        /// </summary>
        private string BuildLayoutDebugLine()
        {
            var minimap = Hud.Render.MinimapUiElement;
            var rectText = minimap == null
                ? "null"
                : minimap.Rectangle.Left.ToString("0", CultureInfo.InvariantCulture)
                    + "," + minimap.Rectangle.Top.ToString("0", CultureInfo.InvariantCulture)
                    + " " + minimap.Rectangle.Width.ToString("0", CultureInfo.InvariantCulture)
                    + "x" + minimap.Rectangle.Height.ToString("0", CultureInfo.InvariantCulture);

            float ix, iy, isize;
            GetIconRect(out ix, out iy, out isize);

            return "Win=" + Hud.Window.Size.Width.ToString(CultureInfo.InvariantCulture)
                + "x" + Hud.Window.Size.Height.ToString(CultureInfo.InvariantCulture)
                + "  Minimap=" + rectText
                + "  lineHeight=" + _dbgLineHeight.ToString("0.0", CultureInfo.InvariantCulture)
                + "  Fonts=" + (AutoFontSize ? _dbgFontSizes : "fixed")
                + "  Icon=" + ix.ToString("0", CultureInfo.InvariantCulture)
                + "," + iy.ToString("0", CultureInfo.InvariantCulture);
        }

        private void PaintDebug()
        {
            if (DebugFont == null)
                return;

            var now = Hud.Time.Now;
            var x = Hud.Window.Size.Width * DebugX;
            var y = Hud.Window.Size.Height * DebugY;
            var lh = Hud.Window.Size.Height * 0.0175f;

            var lines = new List<string>
            {
                "== FollowerAliveStatus ==",
                "State=" + State + "  Armed=" + Armed + "  Hired=" + _dbgHired + "  Deaths=" + DeathCount.ToString(CultureInfo.InvariantCulture),
                "InGame=" + Hud.Game.IsInGame + " Loading=" + Hud.Game.IsLoading + " Town=" + Hud.Game.IsInTown + " Players=" + Hud.Game.NumberOfPlayersInGame.ToString(CultureInfo.InvariantCulture),
                "HeroDead=" + (Hud.Game.Me != null && Hud.Game.Me.IsDeadSafeCheck) + "  BlindWindow=" + Math.Max(0d, (_blockUntil - now).TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture) + "ms",
                "Follower: " + _dbgHp,
                "HP: " + _dbgHpLine,
                "Flags: " + _dbgFlags,
                "ActorKind.Follower found: " + _dbgKindActors,
                "Gear: " + _dbgGear,
                "SeenAgo=" + (_lastSeenUtc == DateTime.MinValue ? "never" : (now - _lastSeenUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms")
                    + "  AliveAgo=" + (_lastAliveUtc == DateTime.MinValue ? "never" : (now - _lastAliveUtc).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms"),
                "Deaths log=" + BuildDeathLog(),
                BuildLayoutDebugLine(),
            };

            foreach (var line in lines)
            {
                DebugFont.DrawText(line, x, y);
                y += lh;
            }
        }
    }
}
