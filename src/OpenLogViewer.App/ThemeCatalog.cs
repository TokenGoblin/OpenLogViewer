using System.Windows.Media;

namespace OpenLogViewer.App;

/// <summary>
/// The built-in colour schemes.
///
/// Every <see cref="Theme.Series"/> palette here was checked against its own
/// background for lightness range, chroma, contrast, and separation of adjacent
/// entries under protanopia and deuteranopia. The editor schemes therefore do
/// not use their upstream syntax colours verbatim — those sit too light and too
/// close together to tell two traces apart when they cross. Each palette keeps
/// its scheme's hues and its relative saturation, and moves only in lightness.
/// </summary>
public static class ThemeCatalog
{
    public const string DefaultId = "midnight";

    private static readonly Theme[] All =
    [
        new()
        {
            Id = "midnight", Name = "Midnight", Group = ThemeGroup.Dark,
            Background = C("#14171C"), Panel = C("#1B1F26"), PanelAlt = C("#222831"),
            Line = C("#2C333E"), Text = C("#DDE3EA"), Muted = C("#828D9E"),
            Accent = C("#4FC3F7"), Marker = C("#FFB34D"),
            Series = P("#56820F,#9D4CAB,#B58D00,#7582D5,#DE5222,#007FAA,#BF3469,#06A4B5"),
            RampCool = C("#3987E5"), RampWarm = C("#D03B3B"),
        },
        new()
        {
            Id = "graphite", Name = "Graphite", Group = ThemeGroup.Dark,
            Background = C("#17181A"), Panel = C("#1E1F22"), PanelAlt = C("#26282C"),
            Line = C("#34363B"), Text = C("#E2E3E6"), Muted = C("#8B8E95"),
            Accent = C("#7EB6E8"), Marker = C("#E0A44A"),
            Series = P("#B8496A,#2A7DB7,#A87C05,#7F86D3,#55843F,#7C5496,#24A59D,#E16A33"),
            RampCool = C("#4A8FD0"), RampWarm = C("#C4523F"),
        },
        new()
        {
            Id = "daylight", Name = "Daylight", Group = ThemeGroup.Light,
            Background = C("#FFFFFF"), Panel = C("#F4F6F8"), PanelAlt = C("#E8ECF0"),
            Line = C("#CDD5DD"), Text = C("#1B2026"), Muted = C("#5A6672"),
            Accent = C("#0B6BCB"), Marker = C("#B25E00"),
            Series = P("#D32F69,#007AD5,#CC8600,#8C22A8,#2B7A2F,#6D7ED5,#E54918,#23A3B1"),
            RampCool = C("#1E88E5"), RampWarm = C("#D0342C"),
        },
        new()
        {
            Id = "paper", Name = "Paper", Group = ThemeGroup.Light,
            Background = C("#FBF7EF"), Panel = C("#F3EEE3"), PanelAlt = C("#EAE3D5"),
            Line = C("#D8CFBD"), Text = C("#2A241B"), Muted = C("#6B6152"),
            Accent = C("#A85A16"), Marker = C("#8A5A00"),
            Series = P("#31671B,#8272EF,#EA6900,#1263BE,#F55E35,#7124A1,#29A08E,#AA0F55"),
            RampCool = C("#1565C0"), RampWarm = C("#C43B18"),
        },
        new()
        {
            Id = "dracula", Name = "Dracula", Group = ThemeGroup.Editor,
            Background = C("#282A36"), Panel = C("#21222C"), PanelAlt = C("#343746"),
            Line = C("#44475A"), Text = C("#F8F8F2"), Muted = C("#9CA0B0"),
            Accent = C("#BD93F9"), Marker = C("#FFB86C"),
            Series = P("#D3519E,#757B00,#8563B3,#008E37,#687CBF,#F64C4E,#0090A5,#C48132"),
            RampCool = C("#7B8FE8"), RampWarm = C("#FF5555"),
        },
        new()
        {
            Id = "nord", Name = "Nord", Group = ThemeGroup.Editor,
            Background = C("#2E3440"), Panel = C("#272C36"), PanelAlt = C("#3B4252"),
            Line = C("#434C5E"), Text = C("#ECEFF4"), Muted = C("#A0A8B7"),
            Accent = C("#88C0D0"), Marker = C("#EBCB8B"),
            Series = P("#B18D40,#008BA5,#B66951,#23A5A5,#BE6069,#5080BB,#658842,#A5689C"),
            RampCool = C("#5E81AC"), RampWarm = C("#BF616A"),
        },
        new()
        {
            Id = "solarized-dark", Name = "Solarized Dark", Group = ThemeGroup.Editor,
            Background = C("#002B36"), Panel = C("#01313D"), PanelAlt = C("#073642"),
            Line = C("#0E4A57"), Text = C("#EEE8D5"), Muted = C("#93A1A1"),
            Accent = C("#268BD2"), Marker = C("#CB4B16"),
            Series = P("#687800,#666ABD,#D75624,#2CA89F,#DE3431,#0075BA,#B58901,#C13D7A"),
            RampCool = C("#268BD2"), RampWarm = C("#DC322F"),
        },
        new()
        {
            Id = "solarized-light", Name = "Solarized Light", Group = ThemeGroup.Editor,
            Background = C("#FDF6E3"), Panel = C("#F5EED9"), PanelAlt = C("#EEE8D5"),
            Line = C("#DCD4BC"), Text = C("#073642"), Muted = C("#657B83"),
            Accent = C("#268BD2"), Marker = C("#B58900"),
            Series = P("#738402,#6368BA,#E86636,#1E9F96,#E9403A,#1380C6,#C12073,#B38806"),
            RampCool = C("#268BD2"), RampWarm = C("#DC322F"),
        },
        new()
        {
            Id = "monokai", Name = "Monokai", Group = ThemeGroup.Editor,
            Background = C("#272822"), Panel = C("#21221C"), PanelAlt = C("#31322B"),
            Line = C("#414339"), Text = C("#F8F8F2"), Muted = C("#A6A28C"),
            Accent = C("#66D9EF"), Marker = C("#FD971F"),
            Series = P("#018D7A,#FE2D76,#7D61B4,#5F8502,#CC6286,#A19528,#0093A7,#D07801"),
            RampCool = C("#4EA8C4"), RampWarm = C("#F92672"),
        },
        new()
        {
            Id = "one-dark", Name = "One Dark", Group = ThemeGroup.Editor,
            Background = C("#282C34"), Panel = C("#21252B"), PanelAlt = C("#31363F"),
            Line = C("#3E4451"), Text = C("#CDD3DE"), Muted = C("#828997"),
            Accent = C("#61AFEF"), Marker = C("#D19A66"),
            Series = P("#B38C40,#3779AD,#DB6871,#26A4B2,#A2692C,#7E8BD1,#587F38,#9660A6"),
            RampCool = C("#61AFEF"), RampWarm = C("#E06C75"),
        },
        new()
        {
            Id = "gruvbox", Name = "Gruvbox Dark", Group = ThemeGroup.Editor,
            Background = C("#282828"), Panel = C("#1D2021"), PanelAlt = C("#32302F"),
            Line = C("#504945"), Text = C("#EBDBB2"), Muted = C("#A89984"),
            Accent = C("#83A598"), Marker = C("#FE8019"),
            Series = P("#737500,#00989D,#B35707,#6FA05E,#A75970,#B78602,#098165,#E12B18"),
            RampCool = C("#458588"), RampWarm = C("#CC241D"),
        },
        new()
        {
            Id = "tokyo-night", Name = "Tokyo Night", Group = ThemeGroup.Editor,
            Background = C("#1A1B26"), Panel = C("#16161E"), PanelAlt = C("#24283B"),
            Line = C("#2F334D"), Text = C("#C0CAF5"), Muted = C("#787C99"),
            Accent = C("#7AA2F7"), Marker = C("#FF9E64"),
            Series = P("#B4853D,#486CBC,#E1627B,#27A3B4,#B05714,#4A9DCA,#4B7501,#8361B9"),
            RampCool = C("#7AA2F7"), RampWarm = C("#F7768E"),
        },
        new()
        {
            Id = "contrast-dark", Name = "High Contrast Dark", Group = ThemeGroup.HighContrast,
            Background = C("#000000"), Panel = C("#000000"), PanelAlt = C("#1C1C1C"),
            Line = C("#FFFFFF"), Text = C("#FFFFFF"), Muted = C("#E0E0E0"),
            Accent = C("#00E0FF"), Marker = C("#FFD000"),
            Series = P("#00A899,#FB494A,#7B6BFE,#038434,#AC00C5,#AF8E00,#017ABA,#B95600"),
            RampCool = C("#0088FF"), RampWarm = C("#FF3B30"),
        },
        new()
        {
            Id = "contrast-light", Name = "High Contrast Light", Group = ThemeGroup.HighContrast,
            Background = C("#FFFFFF"), Panel = C("#FFFFFF"), PanelAlt = C("#EDEDED"),
            Line = C("#000000"), Text = C("#000000"), Muted = C("#2A2A2A"),
            Accent = C("#0032C8"), Marker = C("#8A3A00"),
            Series = P("#B88E42,#8900B5,#107234,#717DFF,#E7462F,#0359DB,#AD0067,#21A3AD"),
            RampCool = C("#0032C8"), RampWarm = C("#C20000"),
        },
    ];

    public static IReadOnlyList<Theme> Themes => All;

    public static Theme Default => Find(DefaultId);

    /// <summary>The named theme, or the default when the id is unknown.</summary>
    public static Theme Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(t => t.Id == DefaultId);

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Color[] P(string list) => [.. list.Split(',').Select(C)];
}
