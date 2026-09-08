using System;
using System.Collections.Generic;
using System.Globalization;
using Turbo.Plugins.Default;

namespace Turbo.Plugins.GuiSquare
{
    /// <summary>
    /// Etat de vie du follower (suiveur) + compteur de morts DU FOLLOWER uniquement.
    ///
    ///   VERT  = follower vivant
    ///   ROUGE = follower mort (au sol)
    ///   GRIS  = aucun follower engage / etat inconnu (chargement, menu, partie multi, hero mort...)
    ///
    /// Le compteur ne s'incremente QUE sur une transition "vivant -> mort" confirmee du follower.
    /// La mort du heros, les ecrans de chargement et les changements de zone ne comptent jamais.
    /// </summary>

    public enum FollowerStatusIconPosition
    {
        UnderPortrait,          // sous le portrait du heros (haut gauche)
        LeftOfHealthGlobe,      // a gauche du globe de vie (bas gauche)
        RightOfResourceGlobe,   // a droite du globe de ressource (bas droite)
        AboveSkillBar,          // au-dessus de la barre de competences (bas centre)
        UnderMinimapClock,      // sous l'horloge / la minimap (haut droite)
        BelowRbhSessionPanel,   // sous le panneau de session RBH (qui demarre au coin haut-gauche de la minimap)
        Custom                  // position libre : CustomX / CustomY (ratio d'ecran 0..1)
    }

    public enum FollowerStatusIconStyle
    {
        Skull,      // tete de mort coloree selon l'etat
        Dot,        // pastille ronde pleine
        Portrait    // tete du follower + petite pastille d'etat
    }

    public enum FollowerLifeState
    {
        Unknown,      // gris : on ne sait pas encore (chargement, transition, hero mort)
        NoFollower,   // gris : aucun follower engage (ou partie multijoueur)
        Alive,        // vert
        Dead          // rouge
    }

    public class FollowerAliveStatusPlugin : BasePlugin, IInGameTopPainter, IAfterCollectHandler, INewAreaHandler
    {
        // ---------------------------------------------------------------------
        // Reglages d'affichage
        // ---------------------------------------------------------------------

        public FollowerStatusIconPosition Position { get; set; }

        /// <summary>Position libre, en ratio de l'ecran (0..1). Utilise seulement si Position == Custom.</summary>
        public float CustomX { get; set; }
        public float CustomY { get; set; }

        /// <summary>Decalage additionnel applique a toutes les positions, en ratio de la hauteur d'ecran.</summary>
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        /// <summary>Taille de la pastille, en ratio de la hauteur d'ecran (0.026 = ~28px en 1080p).</summary>
        public float IconSizeRatio { get; set; }

        public bool ShowIcon { get; set; }
        public bool ShowCounter { get; set; }
        /// <summary>true = compteur a droite de la pastille, false = compteur dans la pastille.</summary>
        public bool CounterOnRight { get; set; }
        /// <summary>Masque le compteur tant qu'il vaut 0.</summary>
        public bool HideCounterWhenZero { get; set; }
        /// <summary>Apparence de l'icone : tete de mort, pastille, ou portrait du follower.</summary>
        public FollowerStatusIconStyle IconStyle { get; set; }

        /// <summary>
        /// Hauteur du panneau de session RBH, en fraction de la hauteur de la minimap.
        /// Utilise uniquement par Position = BelowRbhSessionPanel. RBH dessine son texte
        /// a partir du coin haut-gauche de Hud.Render.MinimapUiElement, et sa hauteur depend
        /// du nombre de lignes activees dans sa config : ajuste cette valeur si besoin.
        /// </summary>
        public float RbhPanelHeightRatio { get; set; }
        /// <summary>Fait clignoter la pastille quand le follower est mort.</summary>
        public bool BlinkWhenDead { get; set; }
        /// <summary>Cache l'icone quand la grande carte / carte d'acte est ouverte.</summary>
        public bool HideOnMapModes { get; set; }
        /// <summary>Cache completement l'icone quand aucun follower n'est engage (au lieu du gris).</summary>
        public bool HideWhenNoFollower { get; set; }

        public IBrush AliveBrush { get; set; }
        public IBrush DeadBrush { get; set; }
        public IBrush NoFollowerBrush { get; set; }
        public IBrush UnknownBrush { get; set; }
        public IBrush BackgroundBrush { get; set; }
        public IBrush BorderBrush { get; set; }
        /// <summary>Pinceau des evidements de la tete de mort (orbites, nez, dents).</summary>
        public IBrush IconDetailBrush { get; set; }
        public IFont CounterFont { get; set; }
        public IFont CounterDeadFont { get; set; }
        public IFont DebugFont { get; set; }

        // ---------------------------------------------------------------------
        // Reglages de detection (anti faux positifs)
        // ---------------------------------------------------------------------

        /// <summary>SNO des acteurs "hireling" reellement engages (pas les PNJ de ville).</summary>
        public HashSet<ActorSnoEnum> FollowerActorSnos { get; set; }

        /// <summary>true (defaut) : on n'accepte QUE les SNO ci-dessus. false : on accepte aussi tout acteur ActorKind.Follower.</summary>
        public bool StrictSnoMatch { get; set; }

        /// <summary>Considerer la disparition de l'acteur follower (dans le meme monde) comme une mort.</summary>
        public bool TreatVanishAsDeath { get; set; }

        /// <summary>Delai avant de conclure a une mort quand l'acteur a disparu (ms).</summary>
        public int VanishGraceMs { get; set; }

        /// <summary>Periode aveugle apres un changement de zone / nouvelle partie (ms).</summary>
        public int AreaChangeGraceMs { get; set; }

        /// <summary>Periode aveugle apres la mort / resurrection du heros (ms).</summary>
        public int PlayerDeathGraceMs { get; set; }

        /// <summary>Ne rien detecter tant que le heros est mort.</summary>
        public bool IgnoreWhilePlayerDead { get; set; }

        /// <summary>Remettre le compteur a zero a chaque nouvelle partie.</summary>
        public bool ResetCounterOnNewGame { get; set; }

        /// <summary>Annonce vocale a chaque mort du follower.</summary>
        public bool SpeakOnDeath { get; set; }
        public string SpeakOnDeathText { get; set; }

        /// <summary>Affiche un panneau de diagnostic (a activer une fois pour valider en jeu).</summary>
        public bool DebugEnabled { get; set; }
        public float DebugX { get; set; }
        public float DebugY { get; set; }

        // ---------------------------------------------------------------------
        // Etat expose (lecture seule en pratique)
        // ---------------------------------------------------------------------

        public FollowerLifeState State { get; private set; }
        public int DeathCount { get; set; }
        public ActorSnoEnum CurrentFollowerSno { get; private set; }
        public bool Armed { get; private set; } // on a vu le follower vivant => une mort est comptabilisable

        // ---------------------------------------------------------------------
        // Interne
        // ---------------------------------------------------------------------

        private DateTime _blockUntil;      // periode aveugle (transition / mort du heros)
        private DateTime _lastSeenUtc;     // derniere fois que l'acteur follower a ete trouve
        private DateTime _lastAliveUtc;    // derniere fois qu'il a ete vu vivant
        private DateTime _lastDeathUtc;
        private uint _lastFollowerWorldId;
        private bool _playerWasDead;
        private bool _dbgHired;
        private readonly List<DateTime> _deathTimes = new List<DateTime>();

        // diagnostic
        private string _dbgHp = "-";
        private string _dbgHpLine = "-";
        private string _dbgFlags = "-";
        private string _dbgGear = "-";
        private string _dbgKindActors = "-";
        private uint _hpTrackedAcd;
        private float _hpObservedMin;
        private float _hpObservedMax;

        private ITexture _templarTexture;
        private ITexture _scoundrelTexture;
        private ITexture _enchantressTexture;

        public FollowerAliveStatusPlugin()
        {
            Enabled = true;
            Order = 30000;

            Position = FollowerStatusIconPosition.BelowRbhSessionPanel;
            RbhPanelHeightRatio = 0.46f;
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
            BlinkWhenDead = true;
            HideOnMapModes = true;
            HideWhenNoFollower = false;

            StrictSnoMatch = true;
            // La mort est detectee de facon fiable par les PV (IActor.Hitpoints tombe a 0
            // et l'acteur reste present), donc l'heuristique de disparition est inutile.
            TreatVanishAsDeath = false;
            VanishGraceMs = 2500;
            AreaChangeGraceMs = 4000;
            PlayerDeathGraceMs = 3000;
            IgnoreWhilePlayerDead = true;
            ResetCounterOnNewGame = false;

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

            CounterFont     = Hud.Render.CreateFont("tahoma",  7.5f, 255, 240, 240, 240, true, false, 190, 0, 0, 0, true);
            CounterDeadFont = Hud.Render.CreateFont("tahoma",  7.5f, 255, 255, 105, 105, true, false, 190, 0, 0, 0, true);
            DebugFont       = Hud.Render.CreateFont("consolas", 8.5f, 255, 255, 255, 160, false, false, 200, 0, 0, 0, true);

            _templarTexture     = Hud.Texture.GetTexture(3116868919u);
            _scoundrelTexture   = Hud.Texture.GetTexture(441912908u);
            _enchantressTexture = Hud.Texture.GetTexture(2807221403u);
        }

        // =====================================================================
        // Detection (phase de collecte, aucun rendu ici)
        // =====================================================================

        public void OnNewArea(bool newGame, ISnoArea area)
        {
            var now = Hud.Time.Now;

            // toute transition de zone : on desarme et on aveugle la detection un instant
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

            // --- 1) hors partie / chargement / menu -----------------------------
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

            // en mode diagnostic on releve les signaux bruts AVANT tous les filtres,
            // pour pouvoir observer le follower meme quand le heros est mort / en pause
            if (DebugEnabled)
                CollectDebugInfo(FindFollowerActor());

            // --- 1bis) jeu en pause (menu ESC) : les acteurs ne sont plus fiables
            if (Hud.Game.IsPaused)
            {
                _blockUntil = now.AddMilliseconds(AreaChangeGraceMs);
                return;
            }

            // --- 2) partie multijoueur : aucun follower possible ----------------
            if (Hud.Game.NumberOfPlayersInGame > 1)
            {
                Armed = false;
                State = FollowerLifeState.NoFollower;
                CurrentFollowerSno = default(ActorSnoEnum);
                return;
            }

            // --- 3) le heros est mort : on gele tout ---------------------------
            var playerDead = me.IsDeadSafeCheck || me.IsDead;
            if (playerDead)
            {
                _playerWasDead = true;
                _blockUntil = now.AddMilliseconds(PlayerDeathGraceMs);
                if (IgnoreWhilePlayerDead)
                    return; // etat fige, jamais de comptage
            }
            else if (_playerWasDead)
            {
                // on vient de ressusciter : on prolonge la periode aveugle
                _playerWasDead = false;
                _blockUntil = now.AddMilliseconds(PlayerDeathGraceMs);
                Armed = false;
            }

            // --- 4) recherche du follower --------------------------------------
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

            // --- 5) l'acteur est present : source de verite ---------------------
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

            // --- 6) engage mais acteur absent -----------------------------------
            if (!TreatVanishAsDeath)
            {
                // La detection par PV suffit (cf. IsActorAlive) : une absence d'acteur
                // n'est qu'un trou de collecte. On conserve le dernier etat connu, sauf
                // si on n'a jamais rien confirme -> gris.
                if (State != FollowerLifeState.Alive && State != FollowerLifeState.Dead)
                    State = FollowerLifeState.Unknown;
                return;
            }

            if (!Armed)
            {
                // jamais confirme vivant dans cette zone : on ne conclut rien
                if (State != FollowerLifeState.Dead)
                    State = FollowerLifeState.Unknown;
                return;
            }

            if (me.WorldId != _lastFollowerWorldId)
                return; // on a change de monde : disparition normale

            if ((now - _lastSeenUtc).TotalMilliseconds < VanishGraceMs)
                return; // trop tot pour conclure

            RegisterDeath(now);
        }

        private void RegisterDeath(DateTime now)
        {
            if (now < _blockUntil)
                return; // transition de zone / mort du heros : on ne conclut rien

            if (State == FollowerLifeState.Dead)
                return; // deja compte

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

            Armed = false; // il faudra le revoir vivant avant de recompter une mort
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
            // Signal principal : IActor.Hitpoints, renseigne par TurboHUD lui-meme.
            // (l'attribut Hitpoints_Cur n'est PAS expose sur l'acteur follower : il renvoie -1)
            if (actor.Hitpoints > 0.0001f)
                return true;

            // Second signal : si l'attribut est lisible et positif, on considere vivant.
            // On ne conclut a la mort que si les deux sources sont d'accord.
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
        // Rendu
        // =====================================================================

        public void PaintTopInGame(ClipState clipState)
        {
            if (!Enabled)
                return;
            if (clipState != ClipState.BeforeClip)
                return;
            if (Hud.Render.UiHidden)
                return;
            if (!Hud.Game.IsInGame)
                return;
            if (HideOnMapModes && (Hud.Game.MapMode == MapMode.WaypointMap || Hud.Game.MapMode == MapMode.ActMap))
                return;

            if (DebugEnabled)
                PaintDebug();

            var noFollower = State == FollowerLifeState.NoFollower;
            if (HideWhenNoFollower && noFollower)
                return;

            float x, y, size;
            GetIconRect(out x, out y, out size);
            if (size <= 0f)
                return;

            var cx = x + (size / 2f);
            var cy = y + (size / 2f);
            var r = size / 2f;

            var brush = GetStateBrush();
            var opacity = 1.0f;

            if (BlinkWhenDead && State == FollowerLifeState.Dead)
            {
                // pulsation ~1.1s
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
                    var text = DeathCount.ToString(CultureInfo.InvariantCulture);
                    var layout = font.GetTextLayout(text);
                    if (CounterOnRight || !ShowIcon)
                        font.DrawText(layout, x + (ShowIcon ? size * 1.15f : 0f), cy - (layout.Metrics.Height / 2f));
                    else
                        font.DrawText(layout, cx - (layout.Metrics.Width / 2f), cy - (layout.Metrics.Height / 2f));
                }
            }

            // info-bulle au survol
            var hoverW = size * (ShowCounter && CounterOnRight ? 2.4f : 1.0f);
            if (Hud.Window.CursorInsideRect(x, y, hoverW, size))
                Hud.Render.SetHint(BuildHint());
        }

        private static void WithOpacity(IBrush brush, float opacity, Action draw)
        {
            var previous = brush.Opacity;
            brush.Opacity = opacity;
            draw();
            brush.Opacity = previous;
        }

        /// <summary>
        /// Tete de mort dessinee en primitives (crane + machoire en couleur d'etat,
        /// orbites / nez / dents evides en sombre). Meme encombrement qu'une pastille.
        /// </summary>
        private void DrawSkull(float x, float y, float s, IBrush body, float opacity)
        {
            if (BackgroundBrush != null)
                BackgroundBrush.DrawEllipse(x + (s * 0.5f), y + (s * 0.5f), s * 0.59f, s * 0.59f);

            WithOpacity(body, opacity, () =>
            {
                body.DrawEllipse(x + (s * 0.50f), y + (s * 0.42f), s * 0.40f, s * 0.36f); // crane
                body.DrawEllipse(x + (s * 0.50f), y + (s * 0.70f), s * 0.26f, s * 0.22f); // machoire
            });

            var hole = IconDetailBrush;
            if (hole == null)
                return;

            WithOpacity(hole, opacity, () =>
            {
                hole.DrawEllipse(x + (s * 0.325f), y + (s * 0.40f), s * 0.140f, s * 0.150f); // orbite gauche
                hole.DrawEllipse(x + (s * 0.675f), y + (s * 0.40f), s * 0.140f, s * 0.150f); // orbite droite
                hole.DrawEllipse(x + (s * 0.500f), y + (s * 0.585f), s * 0.055f, s * 0.085f); // nez

                // dents
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
            return s + " - follower deaths: " + DeathCount.ToString(CultureInfo.InvariantCulture);
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

                case FollowerStatusIconPosition.BelowRbhSessionPanel:
                    // RBH dessine son texte de session au coin haut-gauche de la minimap
                    // (Hud.Render.MinimapUiElement), vers le bas. On se place dessous.
                    var minimap = Hud.Render.MinimapUiElement;
                    if (minimap != null && minimap.Rectangle.Width > 0f)
                    {
                        rect = minimap.Rectangle;
                        x = rect.Left;
                        y = rect.Top + (rect.Height * RbhPanelHeightRatio);
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
        // Diagnostic
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

                // suivi min/max de IActor.Hitpoints : s'il varie, c'est bien la vie courante
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
            };

            foreach (var line in lines)
            {
                DebugFont.DrawText(line, x, y);
                y += lh;
            }
        }
    }
}
