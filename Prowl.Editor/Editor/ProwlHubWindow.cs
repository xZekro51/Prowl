// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Assets;
using Prowl.Editor.Preferences;
using Prowl.Icons;
using Prowl.Runtime;
using Prowl.Runtime.GUI;
using Prowl.Runtime.GUI.Layout;

namespace Prowl.Editor;

public class ProwlHubWindow : EditorWindow
{
    public Project? SelectedProject;

    private string _searchText = "";
    private string _createName = "";

    private readonly (string, Action)[] _tabs;
    private int _currentTab;
    private bool _createTabOpen;

    private FileDialog _dialog;
    private FileDialogContext _dialogContext;

    // Animation state
    private float _backgroundAnimTime = 0f;
    private readonly List<Particle> _particles = new();
    private float _titleGlowIntensity = 0f;
    private bool _showWelcomeAnimation = true;
    private float _welcomeAnimTime = 0f;

    // Table sorting
    private enum SortBy { Name, Modified, EditorVersion }
    private SortBy _sortBy = SortBy.Modified;
    private bool _sortAscending = false;

    protected override bool Center { get; } = true;
    protected override double Width { get; }
    protected override double Height { get; }
    protected override bool BackgroundFade { get; } = true;
    protected override bool TitleBar { get; } = false;
    protected override bool RoundCorners => false;
    protected override bool LockSize => true;
    protected override double Padding => 0;

    static readonly Color DarkBG = new Color(0.1f, 0.1f, 0.1f, 1.0f);
    static readonly Color SidebarBG = new Color(0.08f, 0.08f, 0.08f, 1.0f);
    static readonly Color TableHeaderBG = new Color(0.14f, 0.14f, 0.16f, 1.0f);
    static readonly Color TableRowAlternate = new Color(0.08f, 0.08f, 0.1f, 1.0f);
    static readonly Color HubBlue = new Color(0.2f, 0.5f, 1.0f, 1.0f);
    static readonly Color AccentPurple = new Color(0.6f, 0.2f, 1.0f, 1.0f);
    static readonly Color AccentCyan = new Color(0.2f, 1.0f, 0.8f, 1.0f);

    // Improved color scheme for better readability
    static readonly Color ReadableWhite = new Color(0.95f, 0.95f, 0.95f, 1.0f);
    static readonly Color ReadableGray = new Color(0.75f, 0.75f, 0.75f, 1.0f);
    static readonly Color ReadableDarkGray = new Color(0.6f, 0.6f, 0.6f, 1.0f);
    static readonly Color HighContrastText = new Color(1.0f, 1.0f, 1.0f, 1.0f);

    // Particle system for background effects
    private struct Particle
    {
        public Vector2 position;
        public Vector2 velocity;
        public float life;
        public float maxLife;
        public Color color;
        public float size;
    }

    public ProwlHubWindow()
    {
        Title = FontAwesome6.Cube + " Hub";

        // Scale window size based on system DPI scaling
        double systemScale = Graphics.GetSystemDpiScale();
        double baseWidth = 1200;
        double baseHeight = 800;
        
        // Apply system scaling to get appropriate window size
        Width = baseWidth / systemScale; // Divide because the UI system will scale it back up
        Height = baseHeight / systemScale;

        _tabs = [
            (FontAwesome6.FolderOpen + "  Projects", DrawProjectsTab),
            (FontAwesome6.Download + "  Installs", DrawInstallsTab),
            (FontAwesome6.BookOpen + "  Learn", DrawLearnTab),
            (FontAwesome6.Users + "  Community", DrawCommunityTab),
            (FontAwesome6.Gear + "  Settings", DrawSettingsTab)
        ];

        InitializeParticles();
    }

    private void InitializeParticles()
    {
        var random = new System.Random();
        for (int i = 0; i < 50; i++)
        {
            _particles.Add(new Particle
            {
                position = new Vector2(random.Next(0, (int)Width), random.Next(0, (int)Height)),
                velocity = new Vector2((float)(random.NextDouble() - 0.5) * 20f, (float)(random.NextDouble() - 0.5) * 20f),
                life = (float)random.NextDouble() * 10f,
                maxLife = 10f,
                color = Color.Lerp(HubBlue, AccentPurple, (float)random.NextDouble()) * 0.3f,
                size = (float)random.NextDouble() * 3f + 1f
            });
        }
    }

    private void UpdateAnimations()
    {
        float deltaTime = (float)Time.deltaTime;
        _backgroundAnimTime += deltaTime;
        _titleGlowIntensity = (float)(Math.Sin(_backgroundAnimTime * 2.0) * 0.5 + 0.5);

        if (_showWelcomeAnimation)
        {
            _welcomeAnimTime += deltaTime;
            if (_welcomeAnimTime > 3f)
                _showWelcomeAnimation = false;
        }

        // Update particles
        var random = new System.Random();
        for (int i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];
            particle.position += particle.velocity * deltaTime;
            particle.life -= deltaTime;

            // Wrap around screen edges
            if (particle.position.x < 0) particle.position.x = (float)Width;
            if (particle.position.x > Width) particle.position.x = 0;
            if (particle.position.y < 0) particle.position.y = (float)Height;
            if (particle.position.y > Height) particle.position.y = 0;

            // Respawn particle
            if (particle.life <= 0)
            {
                particle.position = new Vector2(random.Next(0, (int)Width), random.Next(0, (int)Height));
                particle.velocity = new Vector2((float)(random.NextDouble() - 0.5) * 20f, (float)(random.NextDouble() - 0.5) * 20f);
                particle.life = particle.maxLife;
                particle.color = Color.Lerp(HubBlue, AccentPurple, (float)random.NextDouble()) * 0.3f;
                particle.size = (float)random.NextDouble() * 3f + 1f;
            }

            _particles[i] = particle;
        }
    }

    private void DrawAnimatedBackground()
    {
        var rect = gui.CurrentNode.LayoutData.Rect;
        
        // Draw base background
        gui.Draw2D.DrawRectFilled(rect, DarkBG);

        // Draw animated gradient overlay
        float phase1 = (float)(Math.Sin(_backgroundAnimTime * 0.5) * 0.5 + 0.5);
        float phase2 = (float)(Math.Sin(_backgroundAnimTime * 0.3 + Math.PI) * 0.5 + 0.5);
        
        Color gradTop = Color.Lerp(HubBlue * 0.1f, AccentPurple * 0.1f, phase1);
        Color gradBottom = Color.Lerp(AccentCyan * 0.05f, HubBlue * 0.05f, phase2);
        
        gui.Draw2D.DrawVerticalGradient(rect.Position, new Vector2(rect.x, rect.y + rect.height), (float)rect.width, gradTop, gradBottom);

        // Draw floating particles
        foreach (var particle in _particles)
        {
            float alpha = particle.life / particle.maxLife;
            Color particleColor = particle.color;
            particleColor.a *= alpha;
            
            gui.Draw2D.DrawCircleFilled(particle.position, particle.size, particleColor);
            
            // Add slight glow effect
            gui.Draw2D.DrawCircleFilled(particle.position, particle.size * 1.5f, particleColor * 0.3f);
        }

        // Draw subtle grid pattern
        DrawAnimatedGrid(rect);
    }

    private void DrawAnimatedGrid(Rect rect)
    {
        float gridSize = 50f;
        float animOffset = (float)(_backgroundAnimTime * 10f) % gridSize;
        Color gridColor = Color.white * 0.02f;

        // Vertical lines
        for (float x = -animOffset; x < rect.width + gridSize; x += gridSize)
        {
            gui.Draw2D.DrawLine(new Vector2(rect.x + x, rect.y), new Vector2(rect.x + x, rect.y + rect.height), gridColor, 1f);
        }

        // Horizontal lines
        for (float y = -animOffset; y < rect.height + gridSize; y += gridSize)
        {
            gui.Draw2D.DrawLine(new Vector2(rect.x, rect.y + y), new Vector2(rect.x + rect.width, rect.y + y), gridColor, 1f);
        }
    }

    protected override void Draw()
    {
        UpdateAnimations();

        if (Project.HasProject)
            isOpened = false;

        // Main container with animated background
        using (gui.CurrentNode.Left(0).Top(0).ExpandWidth().ExpandHeight().Enter())
        {
            DrawAnimatedBackground();

            // Welcome animation overlay
            if (_showWelcomeAnimation)
            {
                DrawWelcomeAnimation();
            }

            // Content area with slide animation
            float contentAlpha = _showWelcomeAnimation ? Math.Max(0f, (_welcomeAnimTime - 1f) / 2f) : 1f;
            if (contentAlpha > 0)
            {
                gui.SetZIndex(100, true);
                using (gui.Node("Content").Top(0).ExpandWidth().ExpandHeight().Layout(LayoutType.Row).Enter())
                {
                    // Sidebar with enhanced animations
                    DrawEnhancedSidebar(contentAlpha);

                    // Main content area
                    DrawMainContent(contentAlpha);
                }
                gui.SetZIndex(0, true);
            }
        }
    }

    private void DrawWelcomeAnimation()
    {
        if (_welcomeAnimTime < 3f)
        {
            gui.SetZIndex(1000, true);
            
            float fadeIn = Math.Min(1f, _welcomeAnimTime);
            float fadeOut = _welcomeAnimTime > 2f ? Math.Max(0f, 1f - (_welcomeAnimTime - 2f)) : 1f;
            float alpha = fadeIn * fadeOut;

            var rect = gui.CurrentNode.LayoutData.Rect;
            
            // Background overlay
            gui.Draw2D.DrawRectFilled(rect, Color.black * (alpha * 0.8f));

            // Animated logo and title
            Vector2 center = new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2);
            
            // Pulsing glow effect
            float pulseScale = 1f + (float)(Math.Sin(_welcomeAnimTime * 10f) * 0.1f);
            Color glowColor = HubBlue * (alpha * 0.5f);
            
            gui.Draw2D.DrawCircleFilled(center, 100f * pulseScale, glowColor);
            gui.Draw2D.DrawCircleFilled(center, 80f * pulseScale, AccentPurple * (alpha * 0.3f));

            // Welcome text with typewriter effect - Improved readability
            string welcomeText = "Welcome to Prowl Hub";
            int visibleChars = Math.Min(welcomeText.Length, (int)(_welcomeAnimTime * 8));
            string displayText = welcomeText.Substring(0, visibleChars);
            
            var textRect = new Rect(center.x - 250, center.y + 80, 500, 60);
            gui.Draw2D.DrawText(displayText, 28, textRect, HighContrastText * alpha);

            gui.SetZIndex(0, true);
        }
    }

    private void DrawEnhancedSidebar(float alpha)
    {
        using (gui.Node("Sidebar").Width(280).ExpandHeight().Layout(LayoutType.Column).Spacing(8).Padding(25).Enter())
        {
            var sidebarRect = gui.CurrentNode.LayoutData.Rect;
            
            // Enhanced sidebar background with glow
            gui.Draw2D.DrawRectFilled(sidebarRect, SidebarBG * alpha);
            
            // Add subtle inner glow
            var glowRect = sidebarRect;
            glowRect.x += 5;
            glowRect.width -= 10;
            gui.Draw2D.DrawVerticalGradient(glowRect.Position, new Vector2(glowRect.x, glowRect.y + glowRect.height), (float)glowRect.width, 
                HubBlue * (alpha * 0.1f), Color.clear);

            // Hub title with glow effect - Improved readability
            using (gui.Node("HubTitle").ExpandWidth().Height(70).Padding(0, 15).Enter())
            {
                var titleRect = gui.CurrentNode.LayoutData.Rect;
                Color titleGlow = HubBlue * (_titleGlowIntensity * alpha * 0.5f);
                
                // Glow background
                gui.Draw2D.DrawRectFilled(titleRect, titleGlow, 8f);
                
                // Title text with improved size and contrast
                gui.Draw2D.DrawText(FontAwesome6.Cube + " Prowl Hub", 24, titleRect, HighContrastText * alpha);
            }

            // Animated separator
            using (gui.Node("Separator").ExpandWidth().Height(3).Enter())
            {
                var sepRect = gui.CurrentNode.LayoutData.Rect;
                float gradientProgress = (float)((_backgroundAnimTime * 0.5) % 1.0);
                
                gui.Draw2D.DrawHorizontalGradient(sepRect.Position, new Vector2(sepRect.x + sepRect.width, sepRect.y), (float)sepRect.height,
                    HubBlue * (alpha * gradientProgress), AccentPurple * (alpha * (1f - gradientProgress)));
            }

            // Enhanced tab buttons with better spacing
            for (int i = 0; i < _tabs.Length; i++)
            {
                DrawEnhancedTabButton(i, alpha);
            }
        }
    }

    private void DrawEnhancedTabButton(int index, float alpha)
    {
        using (gui.Node($"Tab_{index}").ExpandWidth().Height(50).Enter())
        {
            bool isSelected = _currentTab == index;
            bool isHovered = gui.IsNodeHovered();
            
            var tabRect = gui.CurrentNode.LayoutData.Rect;
            
            // Animated selection background
            float selectionAnim = gui.AnimateBool(isSelected, 0.3f, EaseType.CubicOut);
            float hoverAnim = gui.AnimateBool(isHovered, 0.2f, EaseType.QuadOut);
            
            // Background with animated glow
            if (selectionAnim > 0.01f)
            {
                Color selectionColor = Color.Lerp(Color.clear, HubBlue * 0.3f, selectionAnim);
                gui.Draw2D.DrawRectFilled(tabRect, selectionColor * alpha, (float)EditorStylePrefs.Instance.ButtonRoundness);
                
                // Add animated border
                Color borderColor = Color.Lerp(Color.clear, HubBlue, selectionAnim);
                gui.Draw2D.DrawRect(tabRect, borderColor * alpha, 2f, (float)EditorStylePrefs.Instance.ButtonRoundness);
            }
            
            if (hoverAnim > 0.01f && !isSelected)
            {
                Color hoverColor = Color.Lerp(Color.clear, Color.white * 0.1f, hoverAnim);
                gui.Draw2D.DrawRectFilled(tabRect, hoverColor * alpha, (float)EditorStylePrefs.Instance.ButtonRoundness);
            }

            if (gui.IsNodePressed())
                _currentTab = index;

            // Enhanced text with better colors and size
            Color textColor = Color.Lerp(ReadableGray, HighContrastText, selectionAnim);
            textColor = Color.Lerp(textColor, AccentCyan, hoverAnim * 0.3f);
            
            var textRect = tabRect;
            textRect.x += 20;
            gui.Draw2D.DrawText(_tabs[index].Item1, 18, textRect, textColor * alpha);
            
            // Add subtle particle trail for selected tab
            if (isSelected)
            {
                DrawTabParticleTrail(tabRect, alpha);
            }
        }
    }

    private void DrawTabParticleTrail(Rect tabRect, float alpha)
    {
        int trailCount = 5;
        for (int i = 0; i < trailCount; i++)
        {
            float offset = (float)((_backgroundAnimTime * 50f + i * 20f) % 100f);
            Vector2 pos = new Vector2(tabRect.x + tabRect.width - 20 + offset * 0.2f, tabRect.y + tabRect.height / 2);
            
            float trailAlpha = 1f - (offset / 100f);
            Color trailColor = HubBlue * (trailAlpha * alpha * 0.5f);
            
            gui.Draw2D.DrawCircleFilled(pos, 2f, trailColor);
        }
    }

    private void DrawMainContent(float alpha)
    {
        using (gui.Node("MainContent").ExpandWidth().ExpandHeight().Enter())
        {
            var contentRect = gui.CurrentNode.LayoutData.Rect;
            
            // Enhanced content background
            gui.Draw2D.DrawRectFilled(contentRect, DarkBG * alpha);
            
            // Add animated corner accents
            DrawCornerAccents(contentRect, alpha);

            _tabs[_currentTab].Item2.Invoke();
        }
    }

    private void DrawCornerAccents(Rect rect, float alpha)
    {
        float accentSize = 50f;
        float animOffset = (float)(Math.Sin(_backgroundAnimTime) * 10f);
        
        // Top-left accent
        var topLeft = new Vector2(rect.x + animOffset, rect.y + animOffset);
        gui.Draw2D.DrawTriangleFilled(topLeft, new Vector2(1, 0), accentSize, HubBlue * (alpha * 0.3f));
        
        // Bottom-right accent
        var bottomRight = new Vector2(rect.x + rect.width - animOffset, rect.y + rect.height - animOffset);
        gui.Draw2D.DrawTriangleFilled(bottomRight, new Vector2(-1, 0), accentSize, AccentPurple * (alpha * 0.3f));
    }

    private void DrawProjectsTab()
    {
        using (gui.Node("ProjectsTab").ExpandWidth().ExpandHeight().Layout(LayoutType.Column).Padding(40).Enter())
        {
            // Header with enhanced animations
            DrawEnhancedProjectsHeader();

            // Projects table with improved effects
            DrawEnhancedProjectsTable();
        }

        // Create project overlay with advanced animations
        if (_createTabOpen)
        {
            DrawEnhancedCreateProjectOverlay();
        }
    }

    private void DrawEnhancedProjectsHeader()
    {
        using (gui.Node("Header").ExpandWidth().Height(70).Layout(LayoutType.Row).Spacing(25).Enter())
        {
            // Projects title with glow effect - Improved readability
            using (gui.Node("Title").FitContentWidth().ExpandHeight().Enter())
            {
                var titleRect = gui.CurrentNode.LayoutData.Rect;
                
                // Add animated underline
                var underlineRect = new Rect(titleRect.x, titleRect.y + titleRect.height - 6, titleRect.width, 4);
                float underlineProgress = (float)((_backgroundAnimTime * 0.8) % 1.0);
                gui.Draw2D.DrawHorizontalGradient(underlineRect.Position, new Vector2(underlineRect.x + underlineRect.width, underlineRect.y), 
                    (float)underlineRect.height, HubBlue * underlineProgress, AccentPurple * (1f - underlineProgress));
                
                gui.Draw2D.DrawText("Projects", 36, titleRect, HighContrastText);
            }

            // Enhanced search box with glow
            DrawEnhancedSearchBox();

            // Spacer
            using (gui.Node("Spacer").ExpandWidth().ExpandHeight().Enter()) { }

            // Enhanced buttons
            DrawEnhancedButton("AddBtn", "Add", 90, Color.white * 0.2f, () => 
                OpenDialog("Add Existing Project", (x) => ProjectCache.Instance.AddProject(new Project(new DirectoryInfo(x)))));
            
            DrawEnhancedButton("NewBtn", "New project", 140, HubBlue, () => _createTabOpen = !_createTabOpen);
        }
    }

    private void DrawEnhancedSearchBox()
    {
        using (gui.Node("SearchContainer").Width(350).Height(40).Top(15).Enter())
        {
            var searchRect = gui.CurrentNode.LayoutData.Rect;
            var interact = gui.GetInteractable(gui.CurrentNode);
            bool isFocused = interact.IsHovered(); // Using IsHovered as proxy since HasFocus doesn't exist
            
            // Animated border and glow
            float focusAnim = gui.AnimateBool(isFocused, 0.3f, EaseType.CubicOut);
            Color borderColor = Color.Lerp(ReadableDarkGray * 0.4f, HubBlue, focusAnim);
            
            gui.Draw2D.DrawRectFilled(searchRect, Color.black * 0.4f, 8);
            gui.Draw2D.DrawRect(searchRect, borderColor, 2 + focusAnim * 2, 8);
            
            // Add glow effect when focused
            if (focusAnim > 0.01f)
            {
                var glowRect = searchRect;
                glowRect.Expand(focusAnim * 5f);
                gui.Draw2D.DrawRect(glowRect, HubBlue * (focusAnim * 0.4f), 1f, 10);
            }

            var inputRect = searchRect;
            inputRect.x += 45;
            inputRect.width -= 55;
            inputRect.y += 3;
            inputRect.height -= 6;

            // Animated search icon - Improved size and contrast
            var iconRect = searchRect;
            iconRect.x += 12;
            iconRect.width = 25;
            float iconScale = 1f + focusAnim * 0.2f;
            Color iconColor = Color.Lerp(ReadableGray, HubBlue, focusAnim);
            gui.Draw2D.DrawText(FontAwesome6.MagnifyingGlass, 16 * iconScale, iconRect, iconColor);

            gui.InputField("SearchInput", ref _searchText, 255, Gui.InputFieldFlags.None, 
                inputRect.x, inputRect.y, inputRect.width, inputRect.height, EditorGUI.InputStyle);
        }
    }

    private void DrawEnhancedButton(string id, string text, double width, Color baseColor, System.Action onPressed)
    {
        using (gui.Node(id).Width(width).Height(40).Top(15).Enter())
        {
            bool isHovered = gui.IsNodeHovered();
            bool isPressed = gui.IsNodePressed();
            
            float hoverAnim = gui.AnimateBool(isHovered, 0.2f, EaseType.QuadOut);
            float pressAnim = gui.AnimateBool(isPressed, 0.1f, EaseType.QuadInOut);
            
            Color btnColor = Color.Lerp(baseColor, baseColor * 1.3f, hoverAnim);
            btnColor = Color.Lerp(btnColor, baseColor * 0.8f, pressAnim);
            
            var btnRect = gui.CurrentNode.LayoutData.Rect;
            
            // Add animated glow on hover
            if (hoverAnim > 0.01f)
            {
                var glowRect = btnRect;
                glowRect.Expand(hoverAnim * 4f);
                gui.Draw2D.DrawRectFilled(glowRect, btnColor * (hoverAnim * 0.3f), 10);
            }
            
            gui.Draw2D.DrawRectFilled(btnRect, btnColor, 8);
            
            // Add subtle inner highlight
            var highlightRect = btnRect;
            highlightRect.height *= 0.5f;
            gui.Draw2D.DrawRectFilled(highlightRect, Color.white * (0.15f + hoverAnim * 0.15f), 8, CornerRounding.Top);
            
            // Improved text with better size
            gui.Draw2D.DrawText(text, 16, btnRect, HighContrastText);

            if (isPressed)
                onPressed?.Invoke();
        }
    }

    private void DrawEnhancedProjectsTable()
    {
        using (gui.Node("Table").Top(90).ExpandWidth().ExpandHeight(-90).Layout(LayoutType.Column).Enter())
        {
            // Table header with enhanced effects
            DrawEnhancedTableHeader();

            // Table content with improved animations
            DrawEnhancedTableContent();
        }
    }

    private void DrawEnhancedTableHeader()
    {
        using (gui.Node("TableHeader").ExpandWidth().Height(45).Layout(LayoutType.Row).Enter())
        {
            var headerRect = gui.CurrentNode.LayoutData.Rect;
            
            // Enhanced header background
            gui.Draw2D.DrawRectFilled(headerRect, TableHeaderBG);
            gui.Draw2D.DrawHorizontalGradient(headerRect.Position, new Vector2(headerRect.x + headerRect.width, headerRect.y), 
                (float)headerRect.height, HubBlue * 0.1f, AccentPurple * 0.1f);

            // Icon columns with animations - Better spacing
            DrawAnimatedHeaderIcon(FontAwesome6.Star, 60, AccentCyan);
            DrawAnimatedHeaderIcon(FontAwesome6.Link, 60, HubBlue);
            DrawAnimatedHeaderIcon(FontAwesome6.Cube, 60, AccentPurple);

            // Sortable columns with enhanced effects
            DrawEnhancedSortableHeader("Name", SortBy.Name, 420);
            DrawEnhancedSortableHeader("Modified", SortBy.Modified, 160);
            DrawEnhancedSortableHeader("Editor version", SortBy.EditorVersion, 160);

            // Actions column
            using (gui.Node("ActionsHeader").Width(60).ExpandHeight().Enter())
            {
                // Empty header for actions column
            }
        }
    }

    private void DrawAnimatedHeaderIcon(string icon, double width, Color accentColor)
    {
        using (gui.Node($"Icon_{icon}").Width(width).ExpandHeight().Enter())
        {
            var iconRect = gui.CurrentNode.LayoutData.Rect;
            bool isHovered = gui.IsNodeHovered();
            
            float hoverAnim = gui.AnimateBool(isHovered, 0.3f, EaseType.CubicOut);
            Color iconColor = Color.Lerp(ReadableGray, accentColor, hoverAnim);
            
            // Improved icon size for better visibility
            gui.Draw2D.DrawText(icon, 18, iconRect, iconColor);
            
            // Add pulse effect on hover
            if (hoverAnim > 0.01f)
            {
                gui.Draw2D.DrawCircleFilled(new Vector2(iconRect.x + iconRect.width / 2, iconRect.y + iconRect.height / 2), 
                    18f * hoverAnim, accentColor * (hoverAnim * 0.2f));
            }
        }
    }

    private void DrawEnhancedSortableHeader(string text, SortBy sortType, double width)
    {
        using (gui.Node($"Header_{text}").Width(width).ExpandHeight().Enter())
        {
            bool isHovered = gui.IsNodeHovered();
            bool isSelected = _sortBy == sortType;
            
            float hoverAnim = gui.AnimateBool(isHovered, 0.2f, EaseType.QuadOut);
            float selectedAnim = gui.AnimateBool(isSelected, 0.3f, EaseType.CubicOut);
            
            var headerRect = gui.CurrentNode.LayoutData.Rect;
            
            if (hoverAnim > 0.01f)
                gui.Draw2D.DrawRectFilled(headerRect, Color.white * (0.1f * hoverAnim));
            
            if (selectedAnim > 0.01f)
                gui.Draw2D.DrawRectFilled(headerRect, HubBlue * (0.2f * selectedAnim));

            if (gui.IsNodePressed())
            {
                if (_sortBy == sortType)
                    _sortAscending = !_sortAscending;
                else
                {
                    _sortBy = sortType;
                    _sortAscending = true;
                }
            }

            string displayText = text;
            if (_sortBy == sortType)
            {
                displayText += _sortAscending ? " " + FontAwesome6.CaretUp : " " + FontAwesome6.CaretDown;
            }

            var rect = headerRect;
            rect.x += 15;
            // Improved text color and size for better readability
            Color textColor = Color.Lerp(ReadableGray, HighContrastText, selectedAnim);
            gui.Draw2D.DrawText(displayText, 16, rect, textColor);
        }
    }

    private void DrawEnhancedTableContent()
    {
        using (gui.Node("TableContent").ExpandWidth().ExpandHeight().Clip().Scroll(inputstyle: EditorGUI.InputStyle).Enter())
        {
            using (gui.Node("TableRows").ExpandWidth().FitContentHeight().Layout(LayoutType.Column).Enter())
            {
                var projects = GetSortedProjects();
                
                for (int i = 0; i < projects.Count; i++)
                {
                    var project = projects[i];
                    if (project == null) continue;

                    if (!string.IsNullOrEmpty(_searchText) && 
                        !project.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DrawEnhancedProjectRow(project, i);
                }

                // Add loading animation if no projects
                if (projects.Count == 0)
                {
                    DrawNoProjectsAnimation();
                }
            }
        }
    }

    private void DrawNoProjectsAnimation()
    {
        using (gui.Node("NoProjects").ExpandWidth().Height(250).Enter())
        {
            var rect = gui.CurrentNode.LayoutData.Rect;
            Vector2 center = new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2);
            
            // Animated loading indicator
            Vector4 hubBlueVec = new Vector4(HubBlue.r, HubBlue.g, HubBlue.b, HubBlue.a);
            Vector4 whiteVec = new Vector4(Color.white.r * 0.2f, Color.white.g * 0.2f, Color.white.b * 0.2f, Color.white.a * 0.2f);
            gui.Draw2D.LoadingIndicatorCircle(center, 40, hubBlueVec, whiteVec, 8, 1f);
            
            // Improved text size and contrast
            float textAlpha = (float)(Math.Sin(_backgroundAnimTime * 2f) * 0.3f + 0.7f);
            var textRect = new Rect(center.x - 120, center.y + 60, 240, 40);
            gui.Draw2D.DrawText("No projects found", 18, textRect, ReadableWhite * textAlpha);
        }
    }

    private void DrawEnhancedProjectRow(Project project, int index)
    {
        using (gui.Node($"ProjectRow_{project.Name}_{index}").ExpandWidth().Height(70).Layout(LayoutType.Row).Enter())
        {
            bool isSelected = SelectedProject == project;
            bool isHovered = gui.IsNodeHovered();
            
            float hoverAnim = gui.AnimateBool(isHovered, 0.2f, EaseType.QuadOut);
            float selectAnim = gui.AnimateBool(isSelected, 0.3f, EaseType.CubicOut);

            var rowRect = gui.CurrentNode.LayoutData.Rect;
            
            // Enhanced background with animations
            Color bgColor = index % 2 == 0 ? Color.clear : TableRowAlternate;
            
            if (selectAnim > 0.01f)
                bgColor = Color.Lerp(bgColor, HubBlue * 0.3f, selectAnim);
            
            if (hoverAnim > 0.01f && !isSelected)
                bgColor = Color.Lerp(bgColor, Color.white * 0.05f, hoverAnim);

            if (bgColor.a > 0)
                gui.Draw2D.DrawRectFilled(rowRect, bgColor);
            
            // Add animated side accent for selected row
            if (selectAnim > 0.01f)
            {
                var accentRect = new Rect(rowRect.x, rowRect.y, 5, rowRect.height);
                gui.Draw2D.DrawRectFilled(accentRect, HubBlue * selectAnim);
            }

            // Handle clicks
            if (gui.IsNodePressed())
                SelectedProject = project;

            if (gui.IsPointerDoubleClick(MouseButton.Left) && isHovered)
            {
                // Add visual feedback for double-click
                Project.Open(project);
                isOpened = false;
            }

            // Enhanced row icons with hover effects - Better spacing
            DrawEnhancedRowIcon(FontAwesome6.Star, 60, AccentCyan * 0.4f, isHovered);
            DrawEnhancedRowIcon(FontAwesome6.CloudArrowUp, 60, HubBlue * 0.4f, isHovered);
            DrawEnhancedRowIcon(FontAwesome6.Cube, 60, AccentPurple * 0.4f, isHovered);

            // Name column with enhanced styling - Better text contrast
            using (gui.Node("NameColumn").Width(420).ExpandHeight().Padding(20, 12).Layout(LayoutType.Column).Enter())
            {
                using (gui.Node("ProjectName").ExpandWidth().Height(28).Enter())
                {
                    Color nameColor = Color.Lerp(HighContrastText, AccentCyan, selectAnim * 0.5f);
                    gui.Draw2D.DrawText(project.Name, 20, gui.CurrentNode.LayoutData.Rect, nameColor);
                }

                using (gui.Node("ProjectPath").ExpandWidth().Height(22).Enter())
                {
                    string path = project.ProjectPath;
                    if (path.Length > 60)
                        path = "..." + path.Substring(path.Length - 60);

                    gui.Draw2D.DrawText(path, 15, gui.CurrentNode.LayoutData.Rect, ReadableGray);
                }
            }

            // Modified column - Better text readability
            using (gui.Node("ModifiedColumn").Width(160).ExpandHeight().Padding(12).Enter())
            {
                string timeAgo = GetFormattedLastModifiedTime(project.ProjectDirectory.LastWriteTime);
                gui.Draw2D.DrawText(timeAgo, 15, gui.CurrentNode.LayoutData.Rect, ReadableGray);
            }

            // Editor version column - Improved contrast
            using (gui.Node("VersionColumn").Width(160).ExpandHeight().Padding(12).Layout(LayoutType.Row).Spacing(10).Enter())
            {
                using (gui.Node("VersionText").FitContentWidth().ExpandHeight().Enter())
                {
                    gui.Draw2D.DrawText("2022.3.51f1", 15, gui.CurrentNode.LayoutData.Rect, ReadableGray);
                }

                if (!project.IsValid())
                {
                    using (gui.Node("WarningIcon").Width(25).ExpandHeight().Enter())
                    {
                        // Pulsing warning icon - Better size and contrast
                        float pulseAlpha = (float)(Math.Sin(_backgroundAnimTime * 4f) * 0.3f + 0.7f);
                        gui.Draw2D.DrawText(FontAwesome6.TriangleExclamation, 18, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Yellow * pulseAlpha);
                    }
                }
            }

            // Enhanced actions column
            using (gui.Node("ActionsColumn").Width(60).ExpandHeight().Enter())
            {
                if (isHovered)
                {
                    using (gui.Node("MenuBtn").Width(35).Height(35).Enter())
                    {
                        gui.CurrentNode.Left(12).Top(17.5);
                        
                        bool btnHovered = gui.IsNodeHovered();
                        float btnHoverAnim = gui.AnimateBool(btnHovered, 0.2f, EaseType.QuadOut);

                        if (btnHoverAnim > 0.01f)
                            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.white * (0.15f * btnHoverAnim), 6);

                        if (gui.IsNodePressed())
                            ShowProjectContextMenu(project);

                        Color iconColor = Color.Lerp(ReadableGray, AccentCyan, btnHoverAnim);
                        gui.Draw2D.DrawText(FontAwesome6.EllipsisVertical, 16, gui.CurrentNode.LayoutData.Rect, iconColor);
                    }
                }
            }
        }
    }

    private void DrawEnhancedRowIcon(string icon, double width, Color color, bool rowHovered)
    {
        using (gui.Node($"RowIcon_{icon}").Width(width).ExpandHeight().Enter())
        {
            bool iconHovered = gui.IsNodeHovered();
            float hoverAnim = gui.AnimateBool(iconHovered, 0.2f, EaseType.QuadOut);
            
            Color iconColor = Color.Lerp(color, color * 2f, hoverAnim);
            // Better icon size for visibility
            gui.Draw2D.DrawText(icon, 16, gui.CurrentNode.LayoutData.Rect, iconColor);
            
            // Add subtle glow on hover
            if (hoverAnim > 0.01f)
            {
                var iconRect = gui.CurrentNode.LayoutData.Rect;
                gui.Draw2D.DrawCircleFilled(new Vector2(iconRect.x + iconRect.width / 2, iconRect.y + iconRect.height / 2), 
                    15f * hoverAnim, iconColor * (hoverAnim * 0.3f));
            }
        }
    }

    private void DrawEnhancedCreateProjectOverlay()
    {
        float overlayAnim = gui.AnimateBool(_createTabOpen, 0.4f, EaseType.CubicOut);
        
        if (overlayAnim > 0.01f)
        {
            // Overlay background with animated fade
            using (gui.Node("CreateOverlay").Left(0).Top(0).ExpandWidth().ExpandHeight().Enter())
            {
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.black * (0.7f * overlayAnim));

                // Enhanced create project panel with slide animation - Better size
                using (gui.Node("CreatePanel").Width(500).Height(600).Enter())
                {
                    float slideOffset = (1f - overlayAnim) * 100f;
                    gui.CurrentNode.Left(Offset.Percentage(0.5f, -250 + slideOffset)).Top(Offset.Percentage(0.5f, -300));

                    var panelRect = gui.CurrentNode.LayoutData.Rect;
                    
                    // Enhanced panel background with glow
                    gui.Draw2D.DrawRectFilled(panelRect, SidebarBG * overlayAnim, 15);
                    gui.Draw2D.DrawRect(panelRect, HubBlue * (overlayAnim * 0.6f), 3f, 15);
                    
                    // Add animated glow around panel
                    var glowRect = panelRect;
                    glowRect.Expand(8f);
                    gui.Draw2D.DrawRect(glowRect, HubBlue * (overlayAnim * 0.4f), 1f, 18);

                    DrawEnhancedCreateProjectContent(overlayAnim);
                }
            }
        }
    }

    private void DrawEnhancedCreateProjectContent(float overlayAnim)
    {
        using (gui.Node("CreateContent").ExpandWidth().ExpandHeight().Layout(LayoutType.Column).Padding(35).Spacing(25).Enter())
        {
            // Enhanced header with glow effect - Better readability
            using (gui.Node("CreateHeader").ExpandWidth().Height(45).Layout(LayoutType.Row).Enter())
            {
                using (gui.Node("CreateTitle").ExpandWidth().ExpandHeight().Enter())
                {
                    var titleRect = gui.CurrentNode.LayoutData.Rect;
                    Color titleColor = Color.Lerp(HighContrastText, AccentCyan, _titleGlowIntensity * 0.3f);
                    gui.Draw2D.DrawText("Create project", 28, titleRect, titleColor * overlayAnim);
                }

                using (gui.Node("CloseBtn").Width(35).Height(35).Enter())
                {
                    bool closeHovered = gui.IsNodeHovered();
                    float closeHoverAnim = gui.AnimateBool(closeHovered, 0.2f, EaseType.QuadOut);
                    
                    if (closeHoverAnim > 0.01f)
                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Red * (0.8f * closeHoverAnim * overlayAnim), 6);

                    if (gui.IsNodePressed())
                        _createTabOpen = false;

                    gui.Draw2D.DrawText(FontAwesome6.Xmark, 18, gui.CurrentNode.LayoutData.Rect, HighContrastText * overlayAnim);
                }
            }

            // Enhanced template preview with animations - Better size
            using (gui.Node("TemplatePreview").ExpandWidth().Height(180).Enter())
            {
                var previewRect = gui.CurrentNode.LayoutData.Rect;
                
                // Animated background
                gui.Draw2D.DrawRectFilled(previewRect, HubBlue * (0.3f * overlayAnim), 10);
                gui.Draw2D.DrawRect(previewRect, HubBlue * (0.7f * overlayAnim), 2, 10);
                
                // Floating icon with rotation
                float iconRotation = (float)(_backgroundAnimTime * 30f);
                Vector2 iconCenter = new Vector2(previewRect.x + previewRect.width / 2, previewRect.y + previewRect.height / 2);
                
                // Add multiple layers for depth - Better icon sizes
                gui.Draw2D.DrawText(FontAwesome6.PuzzlePiece, 80, new Rect(iconCenter.x - 40, iconCenter.y - 40, 80, 80), AccentPurple * (overlayAnim * 0.4f));
                gui.Draw2D.DrawText(FontAwesome6.PuzzlePiece, 70, new Rect(iconCenter.x - 35, iconCenter.y - 35, 70, 70), HighContrastText * overlayAnim);
            }

            // Enhanced project name input
            DrawEnhancedInputSection("Project name", ref _createName, "ProjectNameInput", overlayAnim);

            // Enhanced location section
            DrawEnhancedLocationSection(overlayAnim);

            // Spacer
            using (gui.Node("Spacer").ExpandWidth().ExpandHeight().Enter()) { }

            // Enhanced create button
            DrawEnhancedCreateButton(overlayAnim);
        }
    }

    private void DrawEnhancedInputSection(string label, ref string value, string inputId, float alpha)
    {
        using (gui.Node("InputSection").ExpandWidth().Height(80).Layout(LayoutType.Column).Spacing(12).Enter())
        {
            using (gui.Node("InputLabel").ExpandWidth().Height(25).Enter())
            {
                gui.Draw2D.DrawText(label, 18, gui.CurrentNode.LayoutData.Rect, ReadableWhite * (0.9f * alpha));
            }

            using (gui.Node("InputField").ExpandWidth().Height(45).Enter())
            {
                var inputRect = gui.CurrentNode.LayoutData.Rect;
                var interact = gui.GetInteractable(gui.CurrentNode);
                bool isFocused = interact.IsHovered(); // Using IsHovered as proxy since HasFocus doesn't exist
                
                float focusAnim = gui.AnimateBool(isFocused, 0.3f, EaseType.CubicOut);
                
                gui.Draw2D.DrawRectFilled(inputRect, Color.black * (0.4f * alpha), 8);
                
                Color borderColor = Color.Lerp(ReadableDarkGray * 0.5f, HubBlue, focusAnim);
                gui.Draw2D.DrawRect(inputRect, borderColor * alpha, 2 + focusAnim, 8);

                var textRect = inputRect;
                textRect.x += 12;
                textRect.width -= 24;

                gui.InputField(inputId, ref value, 255, Gui.InputFieldFlags.None,
                    textRect.x, textRect.y, textRect.width, textRect.height, EditorGUI.InputStyle);
            }
        }
    }

    private void DrawEnhancedLocationSection(float alpha)
    {
        using (gui.Node("LocationSection").ExpandWidth().Height(80).Layout(LayoutType.Column).Spacing(12).Enter())
        {
            using (gui.Node("LocationLabel").ExpandWidth().Height(25).Enter())
            {
                gui.Draw2D.DrawText("Location", 18, gui.CurrentNode.LayoutData.Rect, ReadableWhite * (0.9f * alpha));
            }

            using (gui.Node("LocationRow").ExpandWidth().Height(45).Layout(LayoutType.Row).Spacing(12).Enter())
            {
                using (gui.Node("LocationDisplay").ExpandWidth().ExpandHeight().Enter())
                {
                    gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.black * (0.4f * alpha), 8);
                    gui.Draw2D.DrawRect(gui.CurrentNode.LayoutData.Rect, ReadableDarkGray * (0.5f * alpha), 2, 8);

                    string path = ProjectCache.Instance.SavedProjectsFolder;
                    if (path.Length > 35)
                        path = "..." + path.Substring(path.Length - 35);

                    var textRect = gui.CurrentNode.LayoutData.Rect;
                    textRect.x += 12;
                    gui.Draw2D.DrawText(path, 16, textRect, ReadableWhite * (0.8f * alpha));
                }

                DrawEnhancedButton("BrowseBtn", FontAwesome6.FolderOpen, 45, Color.white * 0.3f, 
                    () => OpenDialog("Select Folder", (x) => ProjectCache.Instance.SavedProjectsFolder = x));
            }
        }
    }

    private void DrawEnhancedCreateButton(float alpha)
    {
        using (gui.Node("CreateButton").ExpandWidth().Height(45).Enter())
        {
            bool canCreate = !string.IsNullOrEmpty(_createName) && 
                           Directory.Exists(ProjectCache.Instance.SavedProjectsFolder) && 
                           !Path.Exists(Path.Combine(ProjectCache.Instance.SavedProjectsFolder, _createName));

            bool isHovered = gui.IsNodeHovered();
            float hoverAnim = gui.AnimateBool(isHovered && canCreate, 0.2f, EaseType.QuadOut);
            
            Color btnColor = canCreate ? HubBlue : Color.white * 0.4f;
            btnColor = Color.Lerp(btnColor, btnColor * 1.3f, hoverAnim);
            
            var btnRect = gui.CurrentNode.LayoutData.Rect;
            
            // Add glow effect
            if (canCreate && hoverAnim > 0.01f)
            {
                var glowRect = btnRect;
                glowRect.Expand(hoverAnim * 5f);
                gui.Draw2D.DrawRectFilled(glowRect, btnColor * (hoverAnim * 0.4f * alpha), 10);
            }

            gui.Draw2D.DrawRectFilled(btnRect, btnColor * alpha, 8);
            
            // Add animated progress bar effect on hover
            if (canCreate && hoverAnim > 0.01f)
            {
                var progressRect = btnRect;
                progressRect.height = 4;
                progressRect.y = btnRect.y + btnRect.height - 4;
                progressRect.width *= hoverAnim;
                gui.Draw2D.DrawRectFilled(progressRect, AccentCyan * alpha, 2);
            }
            
            // Improved button text size
            gui.Draw2D.DrawText("Create project", 18, btnRect, HighContrastText * alpha);

            if (gui.IsNodePressed() && canCreate)
            {
                Project project = Project.CreateNew(new DirectoryInfo(Path.Join(ProjectCache.Instance.SavedProjectsFolder, _createName)));
                ProjectCache.Instance.AddProject(project);
                _createTabOpen = false;
                _createName = "";
            }
        }
    }

    private void ShowProjectContextMenu(Project project)
    {
        // TODO: Implement enhanced context menu with animations
        if (gui.IsPointerClick(MouseButton.Right))
        {
            // Basic context menu actions without popup for now
        }
    }

    private void DrawInstallsTab()
    {
        using (gui.Node("InstallsTab").ExpandWidth().ExpandHeight().Padding(40).Enter())
        {
            DrawComingSoonTab("Installs", FontAwesome6.Download);
        }
    }

    private void DrawLearnTab()
    {
        using (gui.Node("LearnTab").ExpandWidth().ExpandHeight().Padding(40).Enter())
        {
            DrawComingSoonTab("Learn", FontAwesome6.BookOpen);
        }
    }

    private void DrawCommunityTab()
    {
        using (gui.Node("CommunityTab").ExpandWidth().ExpandHeight().Padding(40).Enter())
        {
            DrawComingSoonTab("Community", FontAwesome6.Users);
        }
    }

    private void DrawSettingsTab()
    {
        using (gui.Node("SettingsTab").ExpandWidth().ExpandHeight().Padding(40).Enter())
        {
            DrawComingSoonTab("Settings", FontAwesome6.Gear);
        }
    }

    private void DrawComingSoonTab(string tabName, string icon)
    {
        var rect = gui.CurrentNode.LayoutData.Rect;
        Vector2 center = new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2);
        
        // Animated title - Improved readability
        var titleRect = new Rect(center.x - 180, center.y - 120, 360, 70);
        float titleGlow = (float)(Math.Sin(_backgroundAnimTime * 1.5f) * 0.3f + 0.7f);
        gui.Draw2D.DrawText(tabName, 36, titleRect, HighContrastText * titleGlow);

        // Floating icon with rotation - Better size
        float iconRotation = (float)(_backgroundAnimTime * 20f);
        var iconRect = new Rect(center.x - 50, center.y - 50, 100, 100);
        Color iconColor = Color.Lerp(HubBlue, AccentPurple, (float)(Math.Sin(_backgroundAnimTime) * 0.5 + 0.5));
        gui.Draw2D.DrawText(icon, 70, iconRect, iconColor);

        // Animated "coming soon" text - Better contrast
        var comingSoonRect = new Rect(center.x - 120, center.y + 60, 240, 35);
        float comingSoonAlpha = (float)(Math.Sin(_backgroundAnimTime * 2f) * 0.3f + 0.7f);
        gui.Draw2D.DrawText("Coming soon...", 20, comingSoonRect, ReadableWhite * (0.8f * comingSoonAlpha));

        // Add some floating particles around the icon
        DrawTabSpecificParticles(center);
    }

    private void DrawTabSpecificParticles(Vector2 center)
    {
        int particleCount = 8;
        for (int i = 0; i < particleCount; i++)
        {
            float angle = (float)((i / (float)particleCount) * Math.PI * 2 + _backgroundAnimTime);
            float distance = 90f + (float)(Math.Sin(_backgroundAnimTime * 2f + i) * 25f);
            
            Vector2 particlePos = new Vector2(
                center.x + (float)Math.Cos(angle) * distance,
                center.y + (float)Math.Sin(angle) * distance
            );
            
            float alpha = (float)(Math.Sin(_backgroundAnimTime * 3f + i) * 0.5f + 0.5f);
            Color particleColor = Color.Lerp(HubBlue, AccentCyan, (float)Math.Sin(angle)) * (alpha * 0.6f);
            
            gui.Draw2D.DrawCircleFilled(particlePos, 4f, particleColor);
        }
    }

    private List<Project> GetSortedProjects()
    {
        var projects = new List<Project>();
        for (int i = 0; i < ProjectCache.Instance.ProjectsCount; i++)
        {
            var project = ProjectCache.Instance.GetProject(i);
            if (project != null)
                projects.Add(project);
        }

        return _sortBy switch
        {
            SortBy.Name => _sortAscending ? 
                projects.OrderBy(p => p.Name).ToList() : 
                projects.OrderByDescending(p => p.Name).ToList(),
            SortBy.Modified => _sortAscending ? 
                projects.OrderBy(p => p.ProjectDirectory.LastWriteTime).ToList() : 
                projects.OrderByDescending(p => p.ProjectDirectory.LastWriteTime).ToList(),
            SortBy.EditorVersion => _sortAscending ? 
                projects.OrderBy(p => "2022.3.51f1").ToList() : 
                projects.OrderByDescending(p => "2022.3.51f1").ToList(),
            _ => projects
        };
    }

    private void OpenDialog(string title, Action<string> onComplete)
    {
        _dialogContext ??= new();

        _dialogContext.title = title;
        _dialogContext.parentDirectory = new DirectoryInfo(ProjectCache.Instance.SavedProjectsFolder);
        _dialogContext.OnComplete = onComplete;
        _dialogContext.OnCancel = () => _dialogContext.OnComplete = (x) => { };

        EditorGuiManager.Remove(_dialog);
        _dialog = new FileDialog(_dialogContext);
        EditorGuiManager.FocusWindow(_dialog);
    }

    private static string GetFormattedLastModifiedTime(DateTime lastModified)
    {
        TimeSpan timeSinceLastModified = DateTime.Now - lastModified;

        if (timeSinceLastModified.TotalMinutes < 1)
            return "Just now";
        else if (timeSinceLastModified.TotalMinutes < 60)
            return $"{(int)timeSinceLastModified.TotalMinutes} minutes ago";
        else if (timeSinceLastModified.TotalHours < 24)
            return $"{(int)timeSinceLastModified.TotalHours} hours ago";
        else if (timeSinceLastModified.TotalDays < 30)
            return $"{(int)timeSinceLastModified.TotalDays} days ago";
        else if (timeSinceLastModified.TotalDays < 365)
            return $"{(int)(timeSinceLastModified.TotalDays / 30)} months ago";
        else
            return $"{(int)(timeSinceLastModified.TotalDays / 365)} years ago";
    }
}
